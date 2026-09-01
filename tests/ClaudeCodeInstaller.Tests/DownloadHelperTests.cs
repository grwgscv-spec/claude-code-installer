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
}
