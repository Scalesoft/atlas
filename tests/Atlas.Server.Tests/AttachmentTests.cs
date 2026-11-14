using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class AttachmentTests
{
    [Fact]
    public void References_grant_only_canonical_URL_fields_not_prose_or_remote_lookalikes()
    {
        var id = Guid.NewGuid();
        var url = $"/api/attachments/{id}/download";
        Assert.Equal(id, Assert.Single(AttachmentReferences.Extract(JsonSerializer.Serialize(new { document = new { attrs = new { data = new { url } } } }))));
        Assert.Empty(AttachmentReferences.Extract(JsonSerializer.Serialize(new { text = url, src = "https://external.example" + url })));
        Assert.Throws<ApiException>(() => AttachmentReferences.Extract(JsonSerializer.Serialize(new { src = url + "?bypass=true" })));
    }
    [Fact]
    public async Task Rejected_image_and_oversized_stream_leave_no_blob_and_collision_never_deletes_existing_blob()
    {
        using var temp = new Storage();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ApiException>(() => temp.Files.WriteAsync(id, new MemoryStream("<svg onload='evil()'/>"u8.ToArray()), true, default));
        Assert.False(File.Exists(temp.Files.PathFor(id)));
        await temp.Files.WriteAsync(id, new MemoryStream("original"u8.ToArray()), false, default);
        await Assert.ThrowsAsync<IOException>(() => temp.Files.WriteAsync(id, new MemoryStream("replacement"u8.ToArray()), false, default));
        Assert.Equal("original", await File.ReadAllTextAsync(temp.Files.PathFor(id)));
        var oversized = Guid.NewGuid();
        await Assert.ThrowsAsync<ApiException>(() => temp.Files.WriteAsync(oversized, new MemoryStream(new byte[AttachmentFiles.MaximumImageBytes + 1]), true, default));
        Assert.False(File.Exists(temp.Files.PathFor(oversized)));
    }
    [Theory]
    [InlineData("../../secret.txt", "secret.txt")]
    [InlineData("C:\\folder\\evil\r\n.txt", "evil.txt")]
    [InlineData("..", "attachment")]
    [InlineData("faktura‮fdp.exe", "fakturafdp.exe")]
    [InlineData("a⁦b⁩‎​c﻿.pdf", "abc.pdf")]
    [InlineData("⁧‏", "attachment")]
    public void Names_never_control_storage_paths_or_response_headers(string input, string expected)
        => Assert.Equal(expected, AttachmentFiles.SafeName(input));
    [Fact]
    public void Inline_images_require_matching_magic_and_storage_cannot_be_public()
    {
        Assert.Equal("image/gif", AttachmentFiles.DetectImage("GIF89a123456789"u8));
        Assert.Equal("image/webp", AttachmentFiles.DetectImage("RIFF1234WEBPVP8X"u8));
        Assert.Throws<ApiException>(() => AttachmentFiles.DetectImage("<html>not a png"u8));
        var root = Path.Combine(Path.GetTempPath(), "atlas-attachment-test", Guid.NewGuid().ToString("N"));
        var env = new EnvironmentStub(root);
        Assert.Throws<InvalidOperationException>(() => new AttachmentFiles(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Attachments:DataPath"] = Path.Combine(root,"wwwroot","uploads") }).Build(), env));
    }
    internal sealed class Storage : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "atlas-attachment-test", Guid.NewGuid().ToString("N"));
        public AttachmentFiles Files { get; }
        public Storage() => Files = new(new ConfigurationBuilder().Build(), new EnvironmentStub(Root));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    internal sealed class EnvironmentStub(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AttachmentTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root,"wwwroot");
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
