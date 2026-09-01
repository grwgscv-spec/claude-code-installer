using System.Text.Json;

namespace ClaudeCodeInstaller.Core;

public record CcSwitchInstallResult(bool Installed, string InstallerPath);

public interface ICcSwitchInstaller
{
    Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct);
}

public sealed class CcSwitchInstaller : ICcSwitchInstaller
{
    private const string TempDirName = "claude-code-installer";
    private readonly IDownloadHelper _downloader;
    private readonly IProcessRunner _runner;
    private readonly HttpClient _httpClient;

    public CcSwitchInstaller(IDownloadHelper downloader, IProcessRunner runner, HttpClient? httpClient = null)
    {
        _downloader = downloader;
        _runner = runner;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeInstaller/1.0");
    }

    public async Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), TempDirName);

        // 策略 1：GitHub API 解析最新版资产 URL。
        var assetUrl = await TryResolveLatestFromApiAsync(ct);
        if (assetUrl is not null)
        {
            var fileName = Path.GetFileName(new Uri(assetUrl).AbsolutePath);
            if (string.IsNullOrEmpty(fileName)) fileName = VersionInfo.CcSwitchPinnedAsset;
            log?.Report($"已解析 cc-switch 最新资产: {assetUrl}");
            try
            {
                return await DownloadAndInstallAsync(new[] { assetUrl }, tmp, fileName, log, ct);
            }
            catch (DownloadException ex)
            {
                log?.Report($"最新版下载失败，改用固定版本镜像回退: {ex.Message}");
            }
        }
        else
        {
            log?.Report("GitHub API 不可用，改用固定版本镜像下载。");
        }

        // 策略 2：固定版本经镜像下载（回退）。
        var sources = VersionInfo.CcSwitchMirrors.Select(VersionInfo.PinnedCcSwitchUrl).ToList();
        try
        {
            return await DownloadAndInstallAsync(sources, tmp, VersionInfo.CcSwitchPinnedAsset, log, ct);
        }
        catch (DownloadException ex)
        {
            throw new InvalidOperationException(
                $"cc-switch 下载失败（已尝试所有镜像）。\n{ex.Message}", ex);
        }
    }

    private async Task<string?> TryResolveLatestFromApiAsync(CancellationToken ct)
    {
        string json;
        try
        {
            json = await _httpClient.GetStringAsync(VersionInfo.CcSwitchApiUrl, ct);
        }
        catch
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets))
                return null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name) ||
                    !asset.TryGetProperty("browser_download_url", out var url))
                    continue;

                var assetName = name.GetString();
                if (assetName is null) continue;
                // 只取 Windows x64 .msi：跳过 arm64，也跳过 .sig 等非安装文件。
                if (!assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
                if (assetName.Contains("arm64", StringComparison.OrdinalIgnoreCase)) continue;

                return url.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private async Task<CcSwitchInstallResult> DownloadAndInstallAsync(IReadOnlyList<string> sources,
        string destDir, string fileName, IProgress<string>? log, CancellationToken ct)
    {
        // 让底层 DownloadException 原样向上传播；由调用方决定如何包装/报告。
        var installerPath = await _downloader.DownloadFirstAvailableAsync(
            sources, destDir, fileName, null, ct);
        return await InstallAsync(installerPath, log, ct);
    }

    private async Task<CcSwitchInstallResult> InstallAsync(string installerPath,
        IProgress<string>? log, CancellationToken ct)
    {
        var ext = Path.GetExtension(installerPath);
        var isMsi = ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);

        log?.Report("正在静默安装 cc-switch…");
        ProcessResult result = isMsi
            ? await _runner.RunAsync("msiexec.exe",
                new[] { "/i", installerPath, "/qn", "/norestart" }, null, log, ct)
            : await _runner.RunAsync(installerPath, new[] { "/S" }, null, log, ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"cc-switch 安装失败，退出码 {result.ExitCode}: {result.StandardError}");

        log?.Report("cc-switch 安装完成。");
        return new CcSwitchInstallResult(true, installerPath);
    }
}
