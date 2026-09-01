using System.Net;
using ClaudeCodeInstaller.Core;

namespace ClaudeCodeInstaller.Tests;

public class DownloadHelperTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "dl-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FirstSourceSucceeds_DownloadsFileAndReports100()
    {
        var handler = new FakeHttpHandler(FakeHttpHandler.Ok(new byte[] { 1, 2, 3, 4 }));
        var helper = new DownloadHelper(handler);
        var dir = TempDir();
        try
        {
            var progress = new List<int>();
            var path = await helper.DownloadFirstAvailableAsync(
                new[] { "https://mirror.example/node.zip", "https://fallback.example/node.zip" },
                dir, "node.zip",
                new Progress<DownloadProgress>(p => progress.Add(p.Percent)));

            Assert.True(File.Exists(path));
            Assert.Equal(4, new FileInfo(path).Length);
            Assert.Contains(100, progress);
            Assert.Single(handler.RequestedUrls);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FirstSourceFails_FallsBackToSecond()
    {
        var handler = new FakeHttpHandler(FakeHttpHandler.NotFound(), FakeHttpHandler.Ok(new byte[] { 9 }));
        var helper = new DownloadHelper(handler);
        var dir = TempDir();
        try
        {
            var path = await helper.DownloadFirstAvailableAsync(
                new[] { "https://a.example/x.zip", "https://b.example/x.zip" },
                dir, "x.zip");
            Assert.Equal(2, handler.RequestedUrls.Count);
            Assert.StartsWith("https://a.example", handler.RequestedUrls[0]);
            Assert.StartsWith("https://b.example", handler.RequestedUrls[1]);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task AllSourcesFail_ThrowsDownloadException()
    {
        var handler = new FakeHttpHandler(FakeHttpHandler.NotFound(), FakeHttpHandler.NotFound());
        var helper = new DownloadHelper(handler);
        var dir = TempDir();
        try
        {
            var ex = await Assert.ThrowsAsync<DownloadException>(() =>
                helper.DownloadFirstAvailableAsync(new[] { "https://a.example/x", "https://b.example/x" }, dir, "x"));
            Assert.Contains("https://a.example", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TransientFailure_RetriesSameSourceBeforeFallingBack()
    {
        // First attempt dies mid-stream (transient connection drop); second attempt on
        // the same source succeeds. The IOException must NOT propagate immediately.
        var handler = new FakeHttpHandler(
            OkWithDroppingStream(),
            FakeHttpHandler.Ok(new byte[] { 7, 8, 9 }));
        var helper = new DownloadHelper(handler);
        var dir = TempDir();
        try
        {
            var path = await helper.DownloadFirstAvailableAsync(
                new[] { "https://a.example/node.zip", "https://b.example/node.zip" },
                dir, "node.zip");
            Assert.True(File.Exists(path));
            Assert.Equal(3, new FileInfo(path).Length);
            // Both requests went to source A (same source retried); never reached B.
            Assert.All(handler.RequestedUrls, u => Assert.StartsWith("https://a.example", u));
            Assert.Equal(2, handler.RequestedUrls.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static HttpResponseMessage OkWithDroppingStream()
    {
        var content = new StreamContent(new DroppingReadStream());
        content.Headers.ContentLength = 100;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class DroppingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 100;
        public override long Position { get => 0; set { } }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection dropped");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("connection dropped");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("connection dropped");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
