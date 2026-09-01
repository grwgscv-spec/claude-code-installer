using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class ConfigWriterTests
{
    private static string TempProfile() => Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingSettingsFile_CreatesEnvBlock()
    {
        var writer = new ConfigWriter();
        var profile = TempProfile();
        try
        {
            var result = await writer.WriteDeepSeekConfigAsync(profile, "sk-abc", "deepseek-v4-flash");
            Assert.True(result.SettingsFileCreated);
            var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, ".claude", "settings.json")))!.AsObject();
            var env = root["env"]!.AsObject();
            Assert.Equal("https://api.deepseek.com/anthropic", env["ANTHROPIC_BASE_URL"]!.GetValue<string>());
            Assert.Equal("sk-abc", env["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
            Assert.Equal("deepseek-v4-flash", env["ANTHROPIC_MODEL"]!.GetValue<string>());
            Assert.Equal("deepseek-v4-flash", env["ANTHROPIC_SMALL_FAST_MODEL"]!.GetValue<string>());
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task ExistingSettings_PreservesOtherKeys_AndBacksUp()
    {
        var writer = new ConfigWriter();
        var profile = TempProfile();
        try
        {
            var dir = Path.Combine(profile, ".claude");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"),
                """{"env":{"MY_CUSTOM_KEY":"keep","ANTHROPIC_MODEL":"old"}}""");

            var result = await writer.WriteDeepSeekConfigAsync(profile, "sk-x", "deepseek-chat");

            var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "settings.json")))!.AsObject();
            var env = root["env"]!.AsObject();
            Assert.Equal("keep", env["MY_CUSTOM_KEY"]!.GetValue<string>());
            Assert.Equal("deepseek-chat", env["ANTHROPIC_MODEL"]!.GetValue<string>());
            Assert.NotNull(result.SettingsBackupPath);
            Assert.True(File.Exists(result.SettingsBackupPath));
            Assert.Contains("keep", await File.ReadAllTextAsync(result.SettingsBackupPath!));
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task InvalidExistingJson_IsBackedUpAndReplaced()
    {
        var writer = new ConfigWriter();
        var profile = TempProfile();
        try
        {
            var dir = Path.Combine(profile, ".claude");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), "{ not valid json ");

            var result = await writer.WriteDeepSeekConfigAsync(profile, "sk-x", "deepseek-chat");

            Assert.NotNull(result.SettingsBackupPath);
            var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "settings.json")))!.AsObject();
            Assert.Equal("sk-x", root["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task SeedsCcSwitchConfig_BestEffort()
    {
        var writer = new ConfigWriter();
        var profile = TempProfile();
        try
        {
            var result = await writer.WriteDeepSeekConfigAsync(profile, "sk-x", "deepseek-v4-flash");
            var ccPath = Path.Combine(profile, ".cc-switch", "config.json");
            Assert.True(File.Exists(ccPath));
            var root = JsonNode.Parse(await File.ReadAllTextAsync(ccPath))!.AsObject();
            Assert.Equal("DeepSeek", root["currentProvider"]!.GetValue<string>());
            var provider = root["providers"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "DeepSeek");
            Assert.Equal("sk-x", provider!["apiKey"]!.GetValue<string>());
        }
        finally { Directory.Delete(profile, true); }
    }
}
