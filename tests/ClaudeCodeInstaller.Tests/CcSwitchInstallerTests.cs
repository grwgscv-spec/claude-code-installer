using System.Text;
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class CcSwitchInstallerTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = new();
        public int ExitCode { get; set; } = 0;

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
        {
            Calls.Add((fileName, args));
            return Task.FromResult(new ProcessResult(ExitCode, "", "", false));
        }
    }

    // 权威的 GitHub latest release JSON 形状（task 提供）。
    private const string FakeReleaseJson = """
    {
      "tag_name": "v3.20.1",
      "assets": [
        { "name": "CC-Switch-v3.20.1-Windows-arm64.msi", "browser_download_url": "https://github.com/farion1231/cc-switch/releases/download/v3.20.1/CC-Switch-v3.20.1-Windows-arm64.msi" },
        { "name": "CC-Switch-v3.20.1-Windows.msi.sig", "browser_download_url": "https://github.com/farion1231/cc-switch/releases/download/v3.20.1/CC-Switch-v3.20.1-Windows.msi.sig" },
        { "name": "CC-Switch-v3.20.1-Windows.msi", "browser_download_url": "https://github.com/farion1231/cc-switch/releases/download/v3.20.1/CC-Switch-v3.20.1-Windows.msi" }
      ]
    }
    """;

    private static readonly byte[] FakeMsi = new byte[] { 1, 2, 3 };

    [Fact]
    public async Task ResolvesLatestAsset_AndInstallsSilently()
    {
        var handler = new FakeHttpHandler(
            FakeHttpHandler.Ok(System.Text.Encoding.UTF8.GetBytes(FakeReleaseJson)),
            FakeHttpHandler.Ok(FakeMsi));
        var runner = new FakeProcessRunner();
        // 同一个 handler 同时喂给 DownloadHelper（下载）和 HttpClient（GitHub API 解析）。
        var installer = new CcSwitchInstaller(new DownloadHelper(handler), runner, new HttpClient(handler));

        var result = await installer.EnsureCcSwitchAsync(null, CancellationToken.None);

        Assert.True(result.Installed);
        // 第一次请求：GitHub latest API。
        Assert.StartsWith("https://api.github.com", handler.RequestedUrls[0]);
        // 第二次请求：下载解析出的非 arm64 .msi 资产。
        Assert.Contains("CC-Switch-v3.20.1-Windows.msi", handler.RequestedUrls[1]);
        Assert.DoesNotContain("arm64", handler.RequestedUrls[1]);
        // msiexec 静默安装。
        Assert.Single(runner.Calls);
        Assert.Equal("msiexec.exe", runner.Calls[0].FileName);
        Assert.Contains("/i", runner.Calls[0].Args);
        Assert.Contains("/qn", runner.Calls[0].Args);
    }

    [Fact]
    public async Task ApiFails_FallsBackToPinnedMirrorUrl()
    {
        var handler = new FakeHttpHandler(
            FakeHttpHandler.NotFound(), // GitHub API 返回 404
            FakeHttpHandler.Ok(FakeMsi)); // 首个镜像下载成功
        var runner = new FakeProcessRunner();
        var installer = new CcSwitchInstaller(new DownloadHelper(handler), runner, new HttpClient(handler));

        var result = await installer.EnsureCcSwitchAsync(null, CancellationToken.None);

        Assert.True(result.Installed);
        // 第二次请求走镜像兜底 URL。
        Assert.StartsWith("https://mirror.ghproxy.com/", handler.RequestedUrls[1]);
        Assert.Contains("CC-Switch-v3.20.1-Windows.msi", handler.RequestedUrls[1]);
        Assert.Single(runner.Calls);
        Assert.Equal("msiexec.exe", runner.Calls[0].FileName);
    }

    [Fact]
    public async Task AllMirrorsFail_ThrowsInvalidOperationException()
    {
        // GitHub API 404 → 镜像1/2/3 全部 404。
        var handler = new FakeHttpHandler(
            FakeHttpHandler.NotFound(),  // API
            FakeHttpHandler.NotFound(),  // mirror 1
            FakeHttpHandler.NotFound(),  // mirror 2
            FakeHttpHandler.NotFound()); // mirror 3
        var runner = new FakeProcessRunner();
        var installer = new CcSwitchInstaller(new DownloadHelper(handler), runner, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.EnsureCcSwitchAsync(null, CancellationToken.None));

        Assert.Contains("镜像", ex.Message);
        foreach (var m in VersionInfo.CcSwitchMirrors)
            Assert.Contains(handler.RequestedUrls, u => u.StartsWith(m));
    }

    [Fact]
    public async Task LatestAssetDownloadFails_FallsBackToPinnedMirror()
    {
        // GitHub API 解析出最新资产 → 最新资产下载 404 → 首个镜像下载成功。
        var handler = new FakeHttpHandler(
            FakeHttpHandler.Ok(Encoding.UTF8.GetBytes(FakeReleaseJson)), // API resolves latest
            FakeHttpHandler.NotFound(),                                  // latest asset download fails
            FakeHttpHandler.Ok(new byte[] { 1, 2, 3 }));                 // first mirror succeeds
        var runner = new FakeProcessRunner();
        var installer = new CcSwitchInstaller(new DownloadHelper(handler), runner, new HttpClient(handler));

        var result = await installer.EnsureCcSwitchAsync(null, CancellationToken.None);

        Assert.True(result.Installed);
        Assert.Contains(handler.RequestedUrls, u => u.StartsWith("https://mirror.ghproxy.com/"));
    }
}
