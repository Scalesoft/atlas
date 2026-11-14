using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Streams the decrypted KeePass XML into <see cref="KdbxDatabase"/>. Only meta names, groups and current
/// entries are materialized; History, Binaries, CustomIcons, CustomData, DeletedObjects and any unknown element
/// are walked without keeping content. The walk still unmasks every <c>Protected="True"</c> value in document
/// order, because they all share one inner keystream: skipping a history password without consuming its
/// bytes would garble every later entry.
/// </summary>
internal sealed class KdbxXmlParser
{
    // Legitimate nesting below a group: Entry, History, Entry, String, Value plus the document elements.
    private const int XmlDepthAllowance = 16;
    private static readonly long MaximumTimeSeconds = DateTime.MaxValue.Ticks / TimeSpan.TicksPerSecond;

    private readonly XmlReader m_reader;
    private readonly KdbxInnerStream m_inner;
    private readonly VaultOptions m_options;
    private readonly CancellationToken m_cancellationToken;
    private readonly List<(Guid Uuid, string Name, int Parent)> m_groups = [];
    private readonly List<(KdbxEntry Entry, int Group)> m_entries = [];
    private readonly HashSet<Guid> m_entryIds = [];
    private string m_databaseName = "";
    private bool m_recycleBinEnabled = true;
    private Guid m_recycleBin = Guid.Empty;
    private int m_rootGroup = -1;

    public KdbxXmlParser(XmlReader reader, KdbxInnerStream inner, VaultOptions options, CancellationToken cancellationToken)
    {
        m_reader = reader;
        m_inner = inner;
        m_options = options;
        m_cancellationToken = cancellationToken;
    }

    public KdbxDatabase Parse()
    {
        try
        {
            m_reader.MoveToContent();
            if (m_reader.NodeType != XmlNodeType.Element || m_reader.LocalName != "KeePassFile")
            {
                throw KdbxException.Corrupt();
            }
            ForEachChild(() =>
            {
                switch (m_reader.LocalName)
                {
                    case "Meta":
                        ReadMeta();
                        break;
                    case "Root":
                        ReadRoot();
                        break;
                    default:
                        Skip();
                        break;
                }
            });
            if (m_rootGroup < 0)
            {
                throw KdbxException.Corrupt();
            }
            return Build();
        }
        catch
        {
            foreach (var (entry, _) in m_entries)
            {
                entry.Dispose();
            }
            throw;
        }
    }

    private void ReadMeta() => ForEachChild(() =>
    {
        switch (m_reader.LocalName)
        {
            case "DatabaseName":
                m_databaseName = ReadText();
                break;
            case "RecycleBinEnabled":
                m_recycleBinEnabled = ReadBool();
                break;
            case "RecycleBinUUID":
                m_recycleBin = ReadUuid();
                break;
            default:
                Skip();
                break;
        }
    });

    private void ReadRoot() => ForEachChild(() =>
    {
        if (m_reader.LocalName != "Group")
        {
            Skip();
            return;
        }
        if (m_rootGroup >= 0)
        {
            throw KdbxException.Corrupt();
        }
        m_rootGroup = ReadGroup(-1, 1);
    });

    private int ReadGroup(int parent, int depth)
    {
        if (depth > m_options.MaxGroupDepth)
        {
            throw KdbxException.Limit("group nesting depth", nameof(VaultOptions.MaxGroupDepth));
        }
        if (m_groups.Count >= m_options.MaxEntries)
        {
            throw KdbxException.Limit("number of groups", nameof(VaultOptions.MaxEntries));
        }
        var index = m_groups.Count;
        m_groups.Add((Guid.Empty, "", parent));
        // Children are linked by index, so UUID and Name may appear after nested groups and entries.
        ForEachChild(() =>
        {
            switch (m_reader.LocalName)
            {
                case "UUID":
                    m_groups[index] = (ReadUuid(), m_groups[index].Name, parent);
                    break;
                case "Name":
                    m_groups[index] = (m_groups[index].Uuid, ReadText(), parent);
                    break;
                case "Entry":
                    ReadEntry(index);
                    break;
                case "Group":
                    ReadGroup(index, depth + 1);
                    break;
                default:
                    Skip();
                    break;
            }
        });
        return index;
    }

    private void ReadEntry(int group)
    {
        if (m_entries.Count >= m_options.MaxEntries)
        {
            throw KdbxException.Limit("number of entries", nameof(VaultOptions.MaxEntries));
        }
        Guid? uuid = null;
        var title = "";
        var modified = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        byte[]? userName = null;
        byte[]? password = null;
        byte[]? url = null;
        byte[]? notes = null;
        try
        {
            ForEachChild(() =>
            {
                switch (m_reader.LocalName)
                {
                    case "UUID":
                        uuid = ReadUuid();
                        break;
                    case "Times":
                        modified = ReadModificationTime() ?? modified;
                        break;
                    case "String":
                        var (key, value) = ReadString();
                        switch (key)
                        {
                            case "Title":
                                title = Encoding.UTF8.GetString(value);
                                CryptographicOperations.ZeroMemory(value);
                                break;
                            case "UserName":
                                Replace(ref userName, value);
                                break;
                            case "Password":
                                Replace(ref password, value);
                                break;
                            case "URL":
                                Replace(ref url, value);
                                break;
                            case "Notes":
                                Replace(ref notes, value);
                                break;
                            default:
                                CryptographicOperations.ZeroMemory(value);
                                break;
                        }
                        break;
                    default:
                        // History, Binary, AutoType, CustomData, ...
                        Skip();
                        break;
                }
            });
            if (uuid is not { } id || !m_entryIds.Add(id))
            {
                throw KdbxException.Corrupt();
            }
            var entry = new KdbxEntry(id, title, modified, Wrap(userName), Wrap(password), Wrap(url), Wrap(notes));
            m_entries.Add((entry, group));
        }
        catch
        {
            Zero(userName);
            Zero(password);
            Zero(url);
            Zero(notes);
            throw;
        }
    }

    private (string? Key, byte[] Value) ReadString()
    {
        string? key = null;
        byte[]? value = null;
        try
        {
            ForEachChild(() =>
            {
                switch (m_reader.LocalName)
                {
                    case "Key":
                        key = ReadText();
                        break;
                    case "Value":
                        Replace(ref value, ReadBytes());
                        break;
                    default:
                        Skip();
                        break;
                }
            });
        }
        catch
        {
            Zero(value);
            throw;
        }
        return (key, value ?? []);
    }

    private DateTime? ReadModificationTime()
    {
        DateTime? modified = null;
        ForEachChild(() =>
        {
            if (m_reader.LocalName == "LastModificationTime")
            {
                modified = ReadTime();
            }
            else
            {
                Skip();
            }
        });
        return modified;
    }

    /// <summary>
    /// Calls <paramref name="child"/> for each child element of the current element; the callback must consume
    /// the whole child. Text between children is ignored.
    /// </summary>
    private void ForEachChild(Action child)
    {
        if (m_reader.IsEmptyElement)
        {
            m_reader.Read();
            return;
        }
        var depth = m_reader.Depth;
        m_reader.Read();
        while (m_reader.NodeType != XmlNodeType.EndElement || m_reader.Depth != depth)
        {
            Tick();
            if (m_reader.NodeType == XmlNodeType.Element)
            {
                child();
            }
            else
            {
                m_reader.Read();
            }
        }
        m_reader.Read();
    }

    /// <summary>Walks an element without keeping content, unmasking and discarding protected values.</summary>
    private void Skip()
    {
        if (IsProtected())
        {
            CryptographicOperations.ZeroMemory(ReadBytes());
            return;
        }
        if (m_reader.IsEmptyElement)
        {
            m_reader.Read();
            return;
        }
        var depth = m_reader.Depth;
        m_reader.Read();
        while (m_reader.NodeType != XmlNodeType.EndElement || m_reader.Depth != depth)
        {
            Tick();
            if (m_reader.NodeType == XmlNodeType.Element && IsProtected())
            {
                CryptographicOperations.ZeroMemory(ReadBytes());
            }
            else
            {
                m_reader.Read();
            }
        }
        m_reader.Read();
    }

    private void Tick()
    {
        m_cancellationToken.ThrowIfCancellationRequested();
        if (m_reader.EOF)
        {
            throw KdbxException.Corrupt();
        }
        if (m_reader.Depth > m_options.MaxGroupDepth + XmlDepthAllowance)
        {
            throw KdbxException.Limit("XML nesting depth", nameof(VaultOptions.MaxGroupDepth));
        }
    }

    private bool IsProtected() => string.Equals(m_reader.GetAttribute("Protected"), "True", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a leaf element as UTF-8. Like KeePass, any element marked protected is unmasked with the inner
    /// stream, so the keystream position matches the writer's.
    /// </summary>
    private byte[] ReadBytes()
    {
        var isProtected = IsProtected();
        var text = m_reader.ReadElementContentAsString();
        byte[] bytes;
        if (isProtected)
        {
            bytes = Convert.FromBase64String(text);
            if (bytes.Length > m_options.MaxXmlFieldBytes)
            {
                throw KdbxException.Limit("XML field size", nameof(VaultOptions.MaxXmlFieldBytes));
            }
            m_inner.Apply(bytes);
            return bytes;
        }
        bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > m_options.MaxXmlFieldBytes)
        {
            throw KdbxException.Limit("XML field size", nameof(VaultOptions.MaxXmlFieldBytes));
        }
        return bytes;
    }

    private string ReadText()
    {
        var bytes = ReadBytes();
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private bool ReadBool() => string.Equals(ReadText().Trim(), "True", StringComparison.OrdinalIgnoreCase);

    private Guid ReadUuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!Convert.TryFromBase64String(ReadText().Trim(), bytes, out var written) || written != bytes.Length)
        {
            throw KdbxException.Corrupt();
        }
        return new Guid(bytes);
    }

    /// <summary>KDBX 4 stores base64 of little-endian seconds since 0001-01-01 UTC; older writers use ISO 8601.</summary>
    private DateTime ReadTime()
    {
        var text = ReadText().Trim();
        Span<byte> bytes = stackalloc byte[8];
        if (Convert.TryFromBase64String(text, bytes, out var written) && written == bytes.Length)
        {
            var seconds = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            if (seconds < 0 || seconds > MaximumTimeSeconds)
            {
                throw KdbxException.Corrupt();
            }
            return new DateTime(seconds * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }
        throw KdbxException.Corrupt();
    }

    private KdbxDatabase Build()
    {
        Guid? recycleBin = m_recycleBinEnabled && m_recycleBin != Guid.Empty ? m_recycleBin : null;
        var entries = new List<KdbxEntry>(m_entries.Count);
        foreach (var (entry, group) in m_entries)
        {
            var path = new List<string>();
            var inRecycleBin = false;
            for (var current = group; current >= 0; current = m_groups[current].Parent)
            {
                inRecycleBin |= m_groups[current].Uuid == recycleBin;
                if (m_groups[current].Parent >= 0)
                {
                    path.Add(m_groups[current].Name);
                }
            }
            path.Reverse();
            entry.GroupPath = path;
            entry.InRecycleBin = inRecycleBin;
            entries.Add(entry);
        }
        return new KdbxDatabase(m_groups[m_rootGroup].Uuid, m_databaseName, recycleBin, entries);
    }

    private static void Replace(ref byte[]? target, byte[] value)
    {
        Zero(target);
        target = value;
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static KdbxProtectedValue Wrap(byte[]? value) => value is null ? KdbxProtectedValue.Empty() : new KdbxProtectedValue(value);
}
