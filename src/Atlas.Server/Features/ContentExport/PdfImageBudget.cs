using System.Buffers.Binary;
using System.Text.Json;

namespace Atlas.Server.Features.ContentExport;

/// <summary>
/// Bounds decoded raster memory without invoking an image decoder. Animation is not a print format. Two different limits:
/// <see cref="Measure"/> refuses an original whose decode alone would be too large, before anything decodes it (the print
/// preparation decodes one original at a time to scale it down); <see cref="Add(ReadOnlySpan{byte}, string?)"/> sums what
/// actually reaches the print page, which Chromium holds decoded at once: attachments after their scaling to the printed
/// size (<see cref="PdfImagePlan"/>), embedded data images as they are.
/// </summary>
public sealed class PdfImageBudget
{
    public const long MaximumImagePixels = 16_000_000;
    public const long MaximumTotalPixels = 40_000_000;
    private long m_pixels;

    /// <summary>Pixels already admitted to the print page.</summary>
    public long Pixels => m_pixels;

    public void Add(ReadOnlySpan<byte> bytes, string? name = null)
    {
        var (width, height) = Measure(bytes, name);
        var pixels = (long)width * height;
        if (m_pixels + pixels > MaximumTotalPixels)
        {
            throw new ApiException(413, "Obrázky se do PDF nevejdou ani po zmenšení na tiskovou velikost: nejvýše 40 milionů pixelů celkem. Rozdělte stránku na kratší části nebo zmenšete či ořízněte obrázky ve stránce.");
        }
        m_pixels += pixels;
    }

    /// <summary>The header dimensions of a static PNG, JPEG or WebP; refuses one too large to decode before it is decoded.</summary>
    public static (int Width, int Height) Measure(ReadOnlySpan<byte> bytes, string? name = null)
    {
        var (width, height) = Dimensions(bytes);
        if (width <= 0 || height <= 0 || (long)width * height > MaximumImagePixels)
        {
            var subject = name is null ? "Obrázek" : $"Obrázek „{name}“";
            throw new ApiException(413, "{subject} je pro PDF příliš velký: nejvýše 16 milionů pixelů na obrázek ({width} × {height} px). Zmenšete jeho rozlišení.", args: new Dictionary<string, object?> { ["subject"] = subject, ["width"] = width, ["height"] = height });
        }
        return (width, height);
    }

    public void CheckEmbedded(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name is "src" or "photo" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var source = property.Value.GetString()!.Trim();
                        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) AddDataUrl(source);
                    }
                    else CheckEmbedded(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in node.EnumerateArray()) CheckEmbedded(child);
                break;
        }
    }

    public void AddDataUrl(string source)
    {
        var comma = source.IndexOf(',');
        if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw Unsupported();
        try { Add(Convert.FromBase64String(source[(comma + 1)..])); }
        catch (FormatException) { throw Unsupported(); }
    }

    private static (int Width, int Height) Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return Png(bytes);
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216) return Jpeg(bytes);
        if (bytes.Length >= 20 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8,4).SequenceEqual("WEBP"u8)) return WebP(bytes);
        throw Unsupported();
    }

    private static (int, int) Png(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) throw Unsupported();
        for (var offset = 8; offset <= bytes.Length - 12;)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (size > bytes.Length - offset - 12 || bytes.Slice(offset + 4, 4).SequenceEqual("acTL"u8)) throw Unsupported();
            offset += (int)size + 12;
        }
        return (BinaryPrimitives.ReadInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..]));
    }

    private static (int, int) Jpeg(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset < bytes.Length - 1)
        {
            if (bytes[offset++] != 255) throw Unsupported();
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset >= bytes.Length - 2) throw Unsupported();
            var marker = bytes[offset++];
            if (marker is 0xD9 or 0xDA) throw Unsupported();
            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (size < 2 || size > bytes.Length - offset) throw Unsupported();
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (size < 8) throw Unsupported();
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            }
            offset += size;
        }
        throw Unsupported();
    }

    private static (int, int) WebP(ReadOnlySpan<byte> bytes)
    {
        var width = 0;
        var height = 0;
        for (var offset = 12; offset <= bytes.Length - 8;)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (size > bytes.Length - offset - 8) throw Unsupported();
            var type = bytes.Slice(offset, 4);
            var data = bytes.Slice(offset + 8, (int)size);
            var dimensions = WebPChunk(type, data);
            width = Math.Max(width, dimensions.Width);
            height = Math.Max(height, dimensions.Height);
            offset += (int)size + 8 + (int)(size & 1);
        }
        return (width, height);
    }

    private static (int Width, int Height) WebPChunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8)) throw Unsupported();
        if (type.SequenceEqual("VP8X"u8))
        {
            if (data.Length < 10 || (data[0] & 2) != 0) throw Unsupported();
            return (1 + data[4] + (data[5] << 8) + (data[6] << 16), 1 + data[7] + (data[8] << 8) + (data[9] << 16));
        }
        if (type.SequenceEqual("VP8L"u8))
        {
            if (data.Length < 5 || data[0] != 0x2f) throw Unsupported();
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            return (1 + (int)(bits & 0x3fff), 1 + (int)((bits >> 14) & 0x3fff));
        }
        if (type.SequenceEqual("VP8 "u8))
        {
            if (data.Length < 10 || !data.Slice(3, 3).SequenceEqual(new byte[] {0x9d, 1, 0x2a})) throw Unsupported();
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3fff);
        }
        return (0, 0);
    }

    private static ApiException Unsupported() => new(422, "Pro PDF použijte statický obrázek PNG, JPEG nebo WebP. GIF, animace a neznámé obrazové formáty nejprve převeďte na PNG.");
}
