namespace Atlas.Yjs;

/// <summary>
/// Managed decoder of Yjs update v1 payloads (lib0 v1): client struct lists, then the delete set. It decodes every
/// struct, including items the same payload also deletes, which a replica observer would never report. References
/// are interpreted by <c>YjsParentResolver</c> (Atlas.Markdown.Sync) against a <c>YjsDocumentIndex</c> (Atlas.Markdown.Sync).
/// </summary>
internal static class YjsUpdateDecoder
{
    private const byte HasOrigin = 0x80;
    private const byte HasRightOrigin = 0x40;
    private const byte HasParentSub = 0x20;
    private const byte ContentMask = 0x1F;
    private const byte SkipRef = 10;

    public static YjsUpdate Decode(ReadOnlySpan<byte> payload, long maxBytes)
    {
        if (payload.Length == 0) throw new ContentRejectedException(ContentRejectionReason.Malformed, "empty payload");
        if (payload.Length > maxBytes)
            throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, $"payload exceeds {maxBytes} bytes");
        var reader = new Lib0Reader(payload);
        var clientCount = reader.ReadCount();
        var clients = new YClientRange[clientCount];
        var structs = new List<YStruct>();
        for (var client = 0; client < clientCount; client++)
        {
            var structCount = reader.ReadCount();
            var clientId = reader.ReadVarUint();
            var clock = reader.ReadClock();
            if (structs.Count + structCount > YjsLimits.MaxStructsPerPayload)
                throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "too many structs");
            var start = clock;
            var first = structs.Count;
            for (var index = 0; index < structCount; index++)
            {
                var next = ReadStruct(ref reader, clientId, clock);
                structs.Add(next);
                clock = next.End;
            }
            clients[client] = new YClientRange(clientId, start, clock, first, structCount);
        }
        var deleteSet = ReadDeleteSet(ref reader);
        if (!reader.AtEnd) throw new ContentRejectedException(ContentRejectionReason.Malformed, "trailing bytes after the delete set");
        return new YjsUpdate(structs.ToArray(), clients, deleteSet);
    }

    private static YStruct ReadStruct(ref Lib0Reader reader, ulong client, uint clock)
    {
        var start = reader.Position;
        var info = reader.ReadByte();
        var content = info & ContentMask;
        if (content == (byte)YContentKind.Gc)
            return new YStruct { Client = client, Clock = clock, Length = CheckedLength(clock, reader.ReadClock()), Content = YContentKind.Gc,
                ByteStart = start, ByteEnd = reader.Position };
        if (content == SkipRef)
            // A skip marks clocks the sender does not have: integrating the rest can only leave pending structs.
            throw new ContentRejectedException(ContentRejectionReason.ClockGap, "skip struct");
        YId? origin = (info & HasOrigin) != 0 ? ReadId(ref reader) : null;
        YId? rightOrigin = (info & HasRightOrigin) != 0 ? ReadId(ref reader) : null;
        YId? parentId = null;
        string? parentRoot = null;
        string? parentSub = null;
        if (origin is null && rightOrigin is null)
        {
            // Yjs readParentInfo: exactly 1 means a root name, anything else a parent id.
            if (reader.ReadVarUint() == 1) parentRoot = reader.ReadName();
            else parentId = ReadId(ref reader);
            if ((info & HasParentSub) != 0) parentSub = reader.ReadName();
        }
        var contentStart = reader.Position;
        uint length = 1;
        var typeRef = YTypeRef.Array;
        string? name = null;
        var valueOffset = 0;
        var valueLength = 0;
        switch ((YContentKind)content)
        {
            case YContentKind.Deleted:
                length = reader.ReadClock();
                break;
            case YContentKind.Json:
            {
                var count = reader.ReadCount();
                for (var index = 0; index < count; index++) reader.ReadUtf8();
                length = (uint)count;
                break;
            }
            case YContentKind.Binary:
                reader.ReadVarBytes();
                break;
            case YContentKind.String:
                valueOffset = reader.Position;
                length = (uint)reader.ReadStringLength();
                valueLength = reader.Position - valueOffset;
                break;
            case YContentKind.Embed:
                reader.ReadJson();
                break;
            case YContentKind.Format:
                name = reader.ReadName();
                valueOffset = reader.Position;
                reader.ReadJson();
                valueLength = reader.Position - valueOffset;
                break;
            case YContentKind.Type:
            {
                var value = reader.ReadVarUint();
                if (value > (ulong)YTypeRef.XmlText) throw new ContentRejectedException(ContentRejectionReason.Malformed, "unknown type ref");
                typeRef = (YTypeRef)value;
                if (typeRef is YTypeRef.XmlElement or YTypeRef.XmlHook) name = reader.ReadName();
                break;
            }
            case YContentKind.Any:
            {
                var count = reader.ReadCount();
                for (var index = 0; index < count; index++)
                {
                    valueOffset = reader.Position;
                    reader.SkipAny(1);
                    valueLength = reader.Position - valueOffset;
                }
                length = (uint)count;
                break;
            }
            case YContentKind.Doc:
                reader.ReadUtf8();
                reader.SkipAny(1);
                break;
            default:
                throw new ContentRejectedException(ContentRejectionReason.Malformed, "unknown content ref");
        }
        return new YStruct
        {
            Client = client,
            Clock = clock,
            Length = CheckedLength(clock, length),
            Content = (YContentKind)content,
            Origin = origin,
            RightOrigin = rightOrigin,
            ParentId = parentId,
            ParentRoot = parentRoot,
            ParentSub = parentSub,
            TypeRef = typeRef,
            Name = name,
            ValueOffset = valueOffset,
            ValueLength = valueLength,
            ByteStart = start,
            ContentStart = contentStart,
            ByteEnd = reader.Position,
        };
    }

    private static YDeleteRange[] ReadDeleteSet(ref Lib0Reader reader)
    {
        var clientCount = reader.ReadCount();
        var ranges = new List<YDeleteRange>();
        for (var client = 0; client < clientCount; client++)
        {
            var clientId = reader.ReadVarUint();
            var count = reader.ReadCount();
            for (var index = 0; index < count; index++)
            {
                var clock = reader.ReadClock();
                ranges.Add(new YDeleteRange(clientId, clock, CheckedLength(clock, reader.ReadClock())));
            }
        }
        return ranges.ToArray();
    }

    private static YId ReadId(ref Lib0Reader reader)
    {
        var client = reader.ReadVarUint();
        return new YId(client, reader.ReadClock());
    }

    private static uint CheckedLength(uint clock, uint length)
    {
        if (length == 0) throw new ContentRejectedException(ContentRejectionReason.Malformed, "zero-length struct or range");
        if ((ulong)clock + length > uint.MaxValue) throw new ContentRejectedException(ContentRejectionReason.Malformed, "clock overflow");
        return length;
    }
}
