using System.Buffers.Binary;
using System.Text.Json;
using Atlas.Server.Features.ContentExport;
using Xunit;
namespace Atlas.Server.Tests;
public sealed class PdfImageBudgetTests
{
    [Fact]
    public void Rejects_compressed_image_bombs_and_aggregate_decode_memory_before_rendering()
    {
        var budget = new PdfImageBudget();
        Assert.Throws<ApiException>(() => budget.Add(Png(10000, 10000)));
        for (var i = 0; i < 2; i++) budget.Add(Png(4000, 4000));
        Assert.Throws<ApiException>(() => budget.Add(Png(4000, 4000)));
    }
    [Fact]
    public void Checks_embedded_images_and_rejects_animated_or_unknown_formats()
    {
        var budget = new PdfImageBudget();
        budget.AddDataUrl("data:image/png;base64," + Convert.ToBase64String(Png(640, 480)));
        Assert.Throws<ApiException>(() => budget.AddDataUrl("data:image/png;base64," + Convert.ToBase64String(Png(10000, 10000))));
        Assert.Throws<ApiException>(() => budget.Add("GIF89a"u8.ToArray()));
    }
    [Theory]
    [InlineData("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAGCAIAAABxZ0isAAAAFElEQVR4nGM0iqpgwAaYsIrSSQIAmiQBEIcmb70AAAAASUVORK5CYII=")]
    [InlineData("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAGAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDkKKKK9Q8s/9k=")]
    [InlineData("UklGRjQAAABXRUJQVlA4ICgAAACQAQCdASoIAAYAAUAmJYgCdLoAA5gA/vFNr7UwSRlL/43xBe1JgAAA")]
    [InlineData("UklGRiIAAABXRUJQVlA4TBYAAAAvB0ABAAdQrcoUr/8BgCD8b5uI6H8I")]
    [InlineData("UklGRl4AAABXRUJQVlA4WAoAAAAQAAAABwAABQAAQUxQSA8AAAABB1CyiAgABOF/20RE/0MAVlA4ICgAAACQAQCdASoIAAYAAUAmJYgCdLoAA5gA/vFNr7UwSRlL/43xBe1JgAAA")]
    public void Accepts_real_static_rasters(string base64) => new PdfImageBudget().Add(Convert.FromBase64String(base64));

    [Fact]
    public void Text_examples_are_not_images_but_actual_sources_are_checked()
    {
        using var prose = JsonDocument.Parse("""{"type":"text","text":"data:image/png;base64,an example"}""");
        new PdfImageBudget().CheckEmbedded(prose.RootElement);
        using var image = JsonDocument.Parse("""{"type":"image","attrs":{"src":"data:image/png;base64,an example"}}""");
        Assert.Throws<ApiException>(() => new PdfImageBudget().CheckEmbedded(image.RootElement));
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[45];
        new byte[] {137,80,78,71,13,10,26,10}.CopyTo(bytes,0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8),13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20),height);
        "IEND"u8.CopyTo(bytes.AsSpan(37));
        return bytes;
    }
}
