using Atlas.Server.Platform.Storage;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class FileStorageReplacementTests
{
    [Fact]
    public async Task Replacement_is_complete_and_failed_input_preserves_previous_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-replace-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var storage = FileStorages.Open(new(root, "", "", null));
            await storage.PublishNewAsync("guide.pdf", new MemoryStream([1, 2, 3]), CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => storage.PublishReplaceAsync("guide.pdf", new BrokenInput(), CancellationToken.None));
            Assert.Equal(new byte[] { 1, 2, 3 }, await storage.ReadBoundedAsync("guide.pdf", 10, CancellationToken.None));
            using var complete = new MemoryStream([8, 9]);
            await Assert.ThrowsAsync<OperationCanceledException>(() => storage.PublishReplaceAsync("guide.pdf", complete, CancellationToken.None, _ =>
            {
                Assert.Equal(complete.Length, complete.Position);
                throw new OperationCanceledException("Publication authority revoked after upload");
            }));
            Assert.Equal(new byte[] { 1, 2, 3 }, await storage.ReadBoundedAsync("guide.pdf", 10, CancellationToken.None));
            await storage.PublishReplaceAsync("guide.pdf", new MemoryStream([4, 5]), CancellationToken.None);
            Assert.Equal(new byte[] { 4, 5 }, await storage.ReadBoundedAsync("guide.pdf", 10, CancellationToken.None));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BrokenInput : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("Interrupted input"));
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => Task.FromException(new IOException("Interrupted input"));
    }
}
