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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeInstaller/1.0");
    }

    public async Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct)
    {
        var destDir = Path.Combine(Path.GetTempPath(), TempDirName);

        // 策略 1：GitHub latest API 解析最新的非 arm64 .msi 资产。
        var resolvedUrl = await TryResolveLatestFromApiAsync(ct);
        if (resolvedUrl is not null)
        {
            log?.Report($"已解析 cc-switch 最新资产: {resolvedUrl}");
            return await DownloadAndInstallAsync(resolvedUrl, destDir, log, ct);
        }

        // 策略 2：GitHub API 失败，走镜像固定版本兜底。
        log?.Report("GitHub API 不可用，回退到镜像固定版本下载…");
        var sources = VersionInfo.CcSwitchMirrors.Select(VersionInfo.PinnedCcSwitchUrl).ToList();
        string installerPath;
        try
        {
            installerPath = await _downloader.DownloadFirstAvailableAsync(
                sources, destDir, VersionInfo.CcSwitchPinnedAsset, null, ct);
        }
        catch (DownloadException ex)
        {
            throw new InvalidOperationException(
                $"cc-switch 在所有镜像下载后仍失败: {ex.Message}", ex);
        }

        return await InstallAsync(installerPath, log, ct);
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

    private async Task<CcSwitchInstallResult> DownloadAndInstallAsync(string url, string destDir,
        IProgress<string>? log, CancellationToken ct)
    {
        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(fileName)) fileName = VersionInfo.CcSwitchPinnedAsset;

        string installerPath;
        try
        {
            installerPath = await _downloader.DownloadFirstAvailableAsync(
                new[] { url }, destDir, fileName, null, ct);
        }
        catch (DownloadException ex)
        {
            throw new InvalidOperationException(
                $"cc-switch 资产下载失败: {ex.Message}", ex);
        }

        return await InstallAsync(installerPath, log, ct);
    }

    private async Task<CcSwitchInstallResult> InstallAsync(string installerPath,
        IProgress<string>? log, CancellationToken ct)
    {
        var ext = Path.GetExtension(installerPath);
        var isMsi = ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);

        log?.Report($"正在静默安装 cc-switch…");
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
