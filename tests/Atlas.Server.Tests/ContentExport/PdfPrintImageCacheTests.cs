using Atlas.Server.Features.ContentExport;
using Xunit;

namespace Atlas.Server.Tests.ContentExport;

public sealed class PdfPrintImageCacheTests
{
    [Fact]
    public void A_copy_stands_only_for_its_original_at_its_size_and_expires_unused()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-cache", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PdfPrintImageCache(Path.Combine(root, "print-images"), new AttachmentTests.EnvironmentStub(root));
            byte[] original = [1, 2, 3], other = [1, 2, 4];
            var key = PdfPrintImageCache.Key(original, 4000, 3000, 0.5, 0.4);
            Assert.Null(cache.Get(key));

            cache.Put(key, [9, 9], "image/jpeg");
            var copy = cache.Get(key);
            Assert.Equal([9, 9], copy!.Value.Bytes);
            Assert.Equal("image/jpeg", copy.Value.MimeType);
            Assert.Null(cache.Get(PdfPrintImageCache.Key(other, 4000, 3000, 0.5, 0.4)));
            Assert.Null(cache.Get(PdfPrintImageCache.Key(original, 4000, 3000, 0.6, 0.4)));

            var file = Assert.Single(Directory.GetFiles(Path.Combine(root, "print-images")));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - PdfPrintImageCache.Retention - TimeSpan.FromHours(1));
            cache.Prune();
            Assert.Null(cache.Get(key));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
