using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeInstaller.Core;

public record ConfigWriteResult(string? SettingsBackupPath, bool SettingsFileCreated,
    string? CcSwitchConfigPath, bool CcSwitchSeeded, string CcSwitchSeedMessage);

public interface IConfigWriter
{
    Task<ConfigWriteResult> WriteDeepSeekConfigAsync(string userProfileDir, string apiKey,
        string model, CancellationToken ct = default);
}

public sealed class ConfigWriter : IConfigWriter
{
    private static readonly JsonSerializerOptions Indented =
        new() { WriteIndented = true };

    public async Task<ConfigWriteResult> WriteDeepSeekConfigAsync(string userProfileDir, string apiKey,
        string model, CancellationToken ct = default)
    {
        var claudeDir = Path.Combine(userProfileDir, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");

        string? backupPath = null;
        var root = new JsonObject();
        if (File.Exists(settingsPath))
        {
            backupPath = await BackupAsync(settingsPath, ct);
            try
            {
                var text = await File.ReadAllTextAsync(settingsPath, ct);
                if (JsonNode.Parse(text) is JsonObject parsed) root = parsed;
            }
            catch (JsonException)
            {
                // 现有文件不是合法 JSON：保留备份，使用全新的配置覆盖。
            }
        }

        SetEnv(root, "ANTHROPIC_BASE_URL", VersionInfo.DeepSeekBaseUrl);
        SetEnv(root, "ANTHROPIC_AUTH_TOKEN", apiKey);
        SetEnv(root, "ANTHROPIC_MODEL", model);
        SetEnv(root, "ANTHROPIC_SMALL_FAST_MODEL", model);

        var wasCreated = !File.Exists(settingsPath);
        await File.WriteAllTextAsync(settingsPath, root.ToJsonString(Indented), ct);

        var ccSwitch = await SeedCcSwitchAsync(userProfileDir, apiKey, model, ct);
        return new ConfigWriteResult(backupPath, wasCreated, ccSwitch.Path, ccSwitch.Seeded, ccSwitch.Message);
    }

    private static void SetEnv(JsonObject root, string key, string value)
    {
        var env = root["env"] as JsonObject ?? new JsonObject();
        env[key] = value;
        root["env"] = env;
    }

    private static async Task<string> BackupAsync(string path, CancellationToken ct)
    {
        var backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        await Task.Run(() => File.Copy(path, backup), ct);
        return backup;
    }

    private static async Task<(string? Path, bool Seeded, string Message)> SeedCcSwitchAsync(
        string userProfileDir, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var ccDir = Path.Combine(userProfileDir, ".cc-switch");
            Directory.CreateDirectory(ccDir);
            var configPath = Path.Combine(ccDir, "config.json");

            var root = new JsonObject();
            if (File.Exists(configPath) && JsonNode.Parse(await File.ReadAllTextAsync(configPath, ct)) is JsonObject parsed)
                root = parsed;

            var providers = root["providers"] as JsonArray ?? new JsonArray();
            var existing = providers.Where(p => p?["name"]?.GetValue<string>() == "DeepSeek").ToList();
            foreach (var e in existing) providers.Remove(e);

            providers.Add(new JsonObject
            {
                ["name"] = "DeepSeek",
                ["apiKey"] = apiKey,
                ["baseUrl"] = VersionInfo.DeepSeekBaseUrl,
                ["model"] = model,
                ["smallModel"] = model,
            });
            root["providers"] = providers;
            root["currentProvider"] = "DeepSeek";

            await File.WriteAllTextAsync(configPath, root.ToJsonString(Indented), ct);
            return (configPath, true, "已预置 DeepSeek 到 cc-switch");
        }
        catch (Exception ex)
        {
            return (null, false, $"cc-switch 配置预置失败（不影响核心功能）: {ex.Message}");
        }
    }
}
