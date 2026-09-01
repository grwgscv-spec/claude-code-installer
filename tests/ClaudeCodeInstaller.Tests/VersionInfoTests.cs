using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class VersionInfoTests
{
    [Fact]
    public void DefaultModel_Is_DeepSeekV4Flash() =>
        Assert.Equal("deepseek-v4-flash", VersionInfo.DefaultModel);

    [Fact]
    public void DeepSeekBaseUrl_Is_AnthropicCompatibleEndpoint() =>
        Assert.Equal("https://api.deepseek.com/anthropic", VersionInfo.DeepSeekBaseUrl);

    [Fact]
    public void NodeMirrors_Prefer_Npmmirror() =>
        Assert.StartsWith("https://npmmirror.com", VersionInfo.NodeZipSources[0]);

    [Fact]
    public void PinnedCcSwitchUrl_ComposesMirrorPrefixWithGithubUrl() =>
        Assert.Equal(
            "https://mirror.ghproxy.com/https://github.com/farion1231/cc-switch/releases/download/v3.20.1/CC-Switch-v3.20.1-Windows.msi",
            VersionInfo.PinnedCcSwitchUrl("https://mirror.ghproxy.com/"));
}
