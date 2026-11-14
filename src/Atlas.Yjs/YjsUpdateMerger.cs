using System.Buffers;
using System.Text;

namespace Atlas.Yjs;

/// <summary>
/// Yjs <c>mergeUpdates</c> for v1 payloads in managed code: the union of their structs, each clock once (a struct another
/// payload already covered in part is sliced as Yjs slices it), and the union of their delete sets, ported step by step from
/// yjs 13.6 utils/updates.js (the struct order, the slicing, adjacent GCs joined, items never joined). The result applies to a
/// Y.Doc exactly as the payloads applied one after another. Its bytes are those Y.mergeUpdates writes whenever no clock is in two
/// payloads; where payloads overlap with different structs at one clock (a GC in one, an item in another), Yjs's sort comparator
/// answers -1 both ways and the engine's sort picks one, while this merger keeps the payload order: the same deleted clocks, an
/// equivalent document, possibly other bytes (README "Divergences from Yjs").
/// A clock gap (a struct missing before a later one of the same client, which Yjs would encode as a Skip) is refused:
/// a gap-free log is what the content guard admits, and a Skip could only leave pending structs.
/// </summary>
public static class YjsUpdateMerger
{
    public static byte[] Merge(IReadOnlyList<ReadOnlyMemory<byte>> payloads, long maxPayloadBytes)
    {
        if (payloads.Count == 0) throw new ArgumentException("Nothing to merge.", nameof(payloads));
        if (payloads.Count == 1) return payloads[0].ToArray();
        var updates = payloads.Select(payload => YjsUpdateDecoder.Decode(payload.Span, maxPayloadBytes)).ToArray();
        var output = new Writer(payloads);
        // Stable, as JavaScript's Array.prototype.sort; afterwards only the first reader moves, so it is re-inserted.
        var readers = updates.Select((update, index) => new Reader(update, index)).Where(reader => reader.Curr is not null)
            .OrderBy(reader => reader, Comparer<Reader>.Create(Compare)).ToList();
        Piece? currWrite = null;
        while (readers.Count > 0)
        {
            var currDecoder = readers[0];
            Iterate(currDecoder, ref currWrite, output);
            readers.RemoveAt(0);
            if (currDecoder.Curr is null) continue;
            var low = 0;
            var high = readers.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (Compare(readers[middle], currDecoder) < 0) low = middle + 1;
                else high = middle;
            }
            readers.Insert(low, currDecoder);
        }
        if (currWrite is not null) output.WriteStruct(currWrite);
        output.FinishStructs();
        output.WriteDeleteSet(updates);
        return output.ToArray();
    }

    /// <summary>One pass of the Yjs loop body for the reader holding the lowest struct.</summary>
    private static void Iterate(Reader currDecoder, ref Piece? currWrite, Writer output)
    {
        var firstClient = currDecoder.Curr!.Client;
        if (currWrite is not null)
        {
            var curr = currDecoder.Curr;
            var iterated = false;
            // Skip what is already written (higher clients are written first).
            while (curr is not null && curr.End <= currWrite.End && curr.Client >= currWrite.Client)
            {
                curr = currDecoder.Next();
                iterated = true;
            }
            if (curr is null || curr.Client != firstClient || (iterated && curr.Clock > currWrite.End)) return;
            if (firstClient != currWrite.Client)
            {
                output.WriteStruct(currWrite);
                currWrite = curr;
                currDecoder.Next();
            }
            else
            {
                if (currWrite.End < curr.Clock)
                    throw new ContentRejectedException(ContentRejectionReason.ClockGap, $"client {firstClient} misses clocks {currWrite.End}..{curr.Clock}");
                var diff = currWrite.End - curr.Clock;
                if (diff > 0) curr = curr.Slice(diff);
                if (!currWrite.MergeWith(curr))
                {
                    output.WriteStruct(currWrite);
                    currWrite = curr;
                    currDecoder.Next();
                }
            }
        }
        else
        {
            currWrite = currDecoder.Curr;
            currDecoder.Next();
        }
        for (var next = currDecoder.Curr; next is not null && next.Client == firstClient && next.Clock == currWrite.End; next = currDecoder.Next())
        {
            output.WriteStruct(currWrite);
            currWrite = next;
        }
    }

    /// <summary>Yjs's comparator, but 0 (stable: payload order) for different structs at one clock, where Yjs's answers -1 both ways.</summary>
    private static int Compare(Reader left, Reader right)
    {
        var a = left.Curr!;
        var b = right.Curr!;
        return a.Client == b.Client ? a.Clock.CompareTo(b.Clock) : b.Client.CompareTo(a.Client);
    }

    /// <summary>A struct of one payload, possibly without its first <see cref="Offset"/> clocks, or a run of joined GCs.</summary>
    private sealed class Piece(int payload, YStruct source, uint offset, uint length)
    {
        public int Payload { get; } = payload;
        public YStruct Source { get; } = source;
        public uint Offset { get; } = offset;
        public uint Length { get; private set; } = length;
        public ulong Client => Source.Client;
        public uint Clock => Source.Clock + Offset;
        public uint End => Clock + Length;
        public bool IsGc => Source.Content == YContentKind.Gc;

        public Piece Slice(uint diff) => new(Payload, Source, Offset + diff, Length - diff);

        /// <summary>Yjs joins only adjacent GCs here: a lazily read item has no right neighbour, so Item.mergeWith refuses.</summary>
        public bool MergeWith(Piece right)
        {
            if (!IsGc || !right.IsGc) return false;
            Length += right.Length;
            return true;
        }
    }

    private sealed class Reader(YjsUpdate update, int payload)
    {
        private int _next;

        public Piece? Curr { get; private set; } = Read(update, payload, 0);

        public Piece? Next()
        {
            Curr = Read(update, payload, ++_next);
            return Curr;
        }

        private static Piece? Read(YjsUpdate update, int payload, int index)
            => index < update.Structs.Length ? new Piece(payload, update.Structs[index], 0, update.Structs[index].Length) : null;
    }

    /// <summary>lib0 v1 output: per client the struct count, client, first clock and structs, then the delete set.</summary>
    private sealed class Writer(IReadOnlyList<ReadOnlyMemory<byte>> payloads)
    {
        private readonly ArrayBufferWriter<byte> _output = new();
        private readonly List<(int Written, byte[] Bytes)> _clients = [];
        private ArrayBufferWriter<byte> _client = new();
        private ulong _currentClient;
        private int _written;

        public void WriteStruct(Piece piece)
        {
            if (_written > 0 && _currentClient != piece.Client) FlushClient();
            if (_written == 0)
            {
                _currentClient = piece.Client;
                VarUint(_client, piece.Client);
                VarUint(_client, piece.Clock);
            }
            var source = piece.Source;
            var payload = payloads[piece.Payload].Span;
            if (piece.IsGc)
            {
                Byte(_client, 0);
                VarUint(_client, piece.Length);
            }
            else if (piece.Offset == 0)
            {
                // Read lazily, an item with an origin has no parentSub, so Yjs writes it again without that flag.
                var info = payload[source.ByteStart];
                Byte(_client, (info & 0xC0) == 0 ? info : (byte)(info & ~0x20));
                _client.Write(payload[(source.ByteStart + 1)..source.ByteEnd]);
            }
            else
            {
                WriteSlicedItem(payload, source, piece.Offset);
            }
            _written++;
        }

        /// <summary>Item.write of the struct Yjs sliceStruct makes: the origin is the clock before the slice, no parent info.</summary>
        private void WriteSlicedItem(ReadOnlySpan<byte> payload, YStruct source, uint offset)
        {
            var info = (byte)((payload[source.ByteStart] & 0x1F) | 0x80 | (source.RightOrigin is null ? 0 : 0x40) | (source.ParentSub is null ? 0 : 0x20));
            Byte(_client, info);
            VarUint(_client, source.Client);
            VarUint(_client, source.Clock + offset - 1);
            if (source.RightOrigin is { } right)
            {
                VarUint(_client, right.Client);
                VarUint(_client, right.Clock);
            }
            var content = payload[source.ContentStart..source.ByteEnd];
            switch (source.Content)
            {
                case YContentKind.Deleted:
                    VarUint(_client, source.Length - offset);
                    break;
                case YContentKind.String:
                {
                    var reader = new Lib0Reader(content);
                    var text = Encoding.UTF8.GetString(reader.ReadUtf8());
                    var rest = text[(int)offset..];
                    // ContentString.splice: a surrogate pair is never split; both halves get U+FFFD instead.
                    if (char.IsHighSurrogate(text[(int)offset - 1])) rest = "�" + rest[1..];
                    var bytes = Encoding.UTF8.GetBytes(rest);
                    VarUint(_client, (ulong)bytes.Length);
                    _client.Write(bytes);
                    break;
                }
                case YContentKind.Json:
                case YContentKind.Any:
                {
                    var reader = new Lib0Reader(content);
                    var count = reader.ReadCount();
                    for (var index = 0; index < offset; index++)
                    {
                        if (source.Content == YContentKind.Json) reader.ReadUtf8();
                        else reader.SkipAny(1);
                    }
                    VarUint(_client, (ulong)(count - offset));
                    _client.Write(content[reader.Position..]);
                    break;
                }
                default:
                    throw new InvalidOperationException($"A {source.Content} struct has one clock and cannot be sliced.");
            }
        }

        public void FinishStructs()
        {
            FlushClient();
            VarUint(_output, (ulong)_clients.Count);
            foreach (var (written, bytes) in _clients)
            {
                VarUint(_output, (ulong)written);
                _output.Write(bytes);
            }
        }

        /// <summary>mergeDeleteSets + sortAndMergeDeleteSet + writeDeleteSet: ranges sorted and joined, clients descending.</summary>
        public void WriteDeleteSet(IEnumerable<YjsUpdate> updates)
        {
            var clients = updates.SelectMany(update => update.DeleteSet).GroupBy(range => range.Client)
                .OrderByDescending(group => group.Key).ToArray();
            VarUint(_output, (ulong)clients.Length);
            foreach (var client in clients)
            {
                var merged = new List<(uint Clock, uint Length)>();
                foreach (var range in client.OrderBy(range => range.Clock))
                {
                    if (merged.Count > 0 && merged[^1].Clock + merged[^1].Length >= range.Clock)
                    {
                        var left = merged[^1];
                        merged[^1] = (left.Clock, Math.Max(left.Length, range.Clock + range.Length - left.Clock));
                    }
                    else merged.Add((range.Clock, range.Length));
                }
                VarUint(_output, client.Key);
                VarUint(_output, (ulong)merged.Count);
                foreach (var (clock, length) in merged)
                {
                    VarUint(_output, clock);
                    VarUint(_output, length);
                }
            }
        }

        public byte[] ToArray() => _output.WrittenSpan.ToArray();

        private void FlushClient()
        {
            if (_written == 0) return;
            _clients.Add((_written, _client.WrittenSpan.ToArray()));
            _client = new ArrayBufferWriter<byte>();
            _written = 0;
        }

        private static void Byte(ArrayBufferWriter<byte> writer, byte value)
        {
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }

        private static void VarUint(ArrayBufferWriter<byte> writer, ulong value)
        {
            while (value > 0x7F)
            {
                Byte(writer, (byte)(0x80 | (value & 0x7F)));
                value >>= 7;
            }
            Byte(writer, (byte)value);
        }
    }
}
