namespace ClaudeCodeInstaller.Core;

public static class VersionInfo
{
    public const string DefaultModel = "deepseek-v4-flash";
    public const string DeepSeekBaseUrl = "https://api.deepseek.com/anthropic";

    // Node.js 便携版（zip）。版本号需在实现时确认该文件名在 npmmirror 上存在。
    public const string NodeVersion = "v24.16.0";
    public const string NodeZipFileName = $"node-{NodeVersion}-win-x64.zip";
    public static readonly string[] NodeZipSources =
    {
        $"https://npmmirror.com/mirrors/node/{NodeVersion}/{NodeZipFileName}",
        $"https://nodejs.org/dist/{NodeVersion}/{NodeZipFileName}",
    };

    public const string ClaudePackage = "@anthropic-ai/claude-code";
    public static readonly string[] NpmRegistries =
    {
        "https://registry.npmmirror.com",
        "https://registry.npmjs.org",
    };

    // cc-switch（经典版，farion1231/cc-switch）
    public const string CcSwitchRepo = "farion1231/cc-switch";
    public const string CcSwitchApiUrl = "https://api.github.com/repos/farion1231/cc-switch/releases/latest";
    // 固定版本兜底：换版本时改这里 + 下面的资产名。实现时确认实际 tag 与资产名。
    public const string CcSwitchPinnedTag = "v0.3.1";
    public const string CcSwitchPinnedAsset = "cc-switch_0.3.1_x64-setup.exe";
    public static readonly string[] CcSwitchMirrors =
    {
        "https://mirror.ghproxy.com/",
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
    };
    public static string PinnedCcSwitchUrl(string mirrorPrefix) =>
        $"{mirrorPrefix}https://github.com/{CcSwitchRepo}/releases/download/{CcSwitchPinnedTag}/{CcSwitchPinnedAsset}";
}
