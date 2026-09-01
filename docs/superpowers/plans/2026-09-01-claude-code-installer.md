# Claude Code 一键安装器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个可分发给他人的 Windows 单文件 exe：输入 DeepSeek API Key + 选择模型，一键自动安装 Node.js、Claude CLI、cc-switch 并写入 DeepSeek 配置（国内网络多镜像回退）。

**Architecture:** 三层分离。`Core` 类库承载全部可测逻辑（下载、安装、配置写入、步骤编排），全部通过接口解耦以便 TDD；`App` 是薄 WinForms 壳（仅 UI + 事件绑定）；`Tests` 是 xunit 测试。发布时自包含单文件，Core 打成内嵌 DLL。

**Tech Stack:** C# / .NET 9、WinForms、System.Text.Json（JsonNode DOM）、xunit、`dotnet publish` 自包含单文件。

**与规格的偏差（实现期合理优化，已确认）：**
1. **Node 改用便携 zip 而非 MSI**——MSI 需要管理员权限（UAC），便携 zip 解压到用户目录 + 写用户 PATH，**全程免管理员**，更适合分发。
2. **cc-switch 预置 schema 为尽力而为**——不同版本 config.json 结构不同，预置失败仅警告、不阻塞核心功能（claude + DeepSeek 配置已直接写入 settings.json，保证可用）。

---

## 文件结构

```
claude-code-installer/
  ├─ ClaudeCodeInstaller.sln
  ├─ src/
  │  ├─ ClaudeCodeInstaller.Core/            (classlib net9.0，全部业务逻辑)
  │  │  ├─ VersionInfo.cs                    版本/URL/镜像常量
  │  │  ├─ DownloadHelper.cs                 IDownloadHelper + 多源回退
  │  │  ├─ ProcessRunner.cs                  IProcessRunner 外部命令执行
  │  │  ├─ PathManager.cs                    IPathManager 用户 PATH 读写
  │  │  ├─ ConfigWriter.cs                   IConfigWriter settings.json + cc-switch 预置
  │  │  ├─ NodeInstaller.cs                  INodeInstaller 便携 node zip
  │  │  ├─ ClaudeInstaller.cs                IClaudeInstaller npm 装 claude
  │  │  ├─ CcSwitchInstaller.cs              ICcSwitchInstaller GitHub 最新版 + 镜像
  │  │  └─ InstallationEngine.cs             步骤编排 + 事件
  │  └─ ClaudeCodeInstaller.App/             (winforms net9.0-windows)
  │     ├─ Program.cs
  │     └─ MainForm.cs                       纯代码构建 UI
  ├─ tests/ClaudeCodeInstaller.Tests/        (xunit)
  │  ├─ FakeHttpHandler.cs / Fakes.cs        测试替身
  │  ├─ DownloadHelperTests.cs
  │  ├─ ConfigWriterTests.cs
  │  ├─ ProcessRunnerTests.cs
  │  ├─ NodeInstallerTests.cs
  │  ├─ ClaudeInstallerTests.cs
  │  ├─ CcSwitchInstallerTests.cs
  │  └─ InstallationEngineTests.cs
  ├─ build.ps1
  └─ README.md
```

**接口约定**（跨任务一致，禁止改名）：

```csharp
public interface IDownloadHelper
{
    Task<string> DownloadFirstAvailableAsync(IReadOnlyList<string> sources, string destDir,
        string fileName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}
public record DownloadProgress(long BytesReceived, long? TotalBytes, int Percent, string Source);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
        string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default);
}
public record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool Cancelled);

public interface IPathManager
{
    string GetUserPath();
    void AppendUserPath(string dir);
}

public interface INodeInstaller
{
    Task<NodeInstallResult> EnsureNodeAsync(string userProfileDir, IProgress<string>? log, CancellationToken ct);
}
public record NodeInstallResult(bool AlreadyInstalled, string NodeDir, string NpmCmd);

public interface IClaudeInstaller
{
    Task<ClaudeInstallResult> EnsureClaudeAsync(string npmCmd, IProgress<string>? log, CancellationToken ct);
}
public record ClaudeInstallResult(bool AlreadyInstalled, string ClaudeCmd);

public interface ICcSwitchInstaller
{
    Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct);
}
public record CcSwitchInstallResult(bool Installed, string InstallerPath);

public interface IConfigWriter
{
    Task<ConfigWriteResult> WriteDeepSeekConfigAsync(string userProfileDir, string apiKey,
        string model, CancellationToken ct = default);
}
public record ConfigWriteResult(string? SettingsBackupPath, bool SettingsFileCreated,
    string? CcSwitchConfigPath, bool CcSwitchSeeded, string CcSwitchSeedMessage);
```

---

### Task 0: 项目脚手架

**Files:**
- Create: `ClaudeCodeInstaller.sln`
- Create: `src/ClaudeCodeInstaller.Core/ClaudeCodeInstaller.Core.csproj`
- Create: `src/ClaudeCodeInstaller.App/ClaudeCodeInstaller.App.csproj`
- Create: `tests/ClaudeCodeInstaller.Tests/ClaudeCodeInstaller.Tests.csproj`

- [ ] **Step 1: 用 dotnet CLI 生成项目骨架**

```bash
cd /c/Users/1/claude-code-installer
dotnet new sln -n ClaudeCodeInstaller
dotnet new classlib -n ClaudeCodeInstaller.Core -o src/ClaudeCodeInstaller.Core -f net9.0
dotnet new winforms -n ClaudeCodeInstaller.App -o src/ClaudeCodeInstaller.App -f net9.0
dotnet new xunit -n ClaudeCodeInstaller.Tests -o tests/ClaudeCodeInstaller.Tests
dotnet sln add src/ClaudeCodeInstaller.Core src/ClaudeCodeInstaller.App tests/ClaudeCodeInstaller.Tests
dotnet add src/ClaudeCodeInstaller.App reference src/ClaudeCodeInstaller.Core
dotnet add tests/ClaudeCodeInstaller.Tests reference src/ClaudeCodeInstaller.Core
```

- [ ] **Step 2: 删除 winforms 模板自带的 Form1，改为纯代码 UI**

```bash
rm src/ClaudeCodeInstaller.App/Form1.cs src/ClaudeCodeInstaller.App/Form1.Designer.cs
```

- [ ] **Step 3: 配置 App 的 csproj 为自包含单文件**

改写 `src/ClaudeCodeInstaller.App/ClaudeCodeInstaller.App.csproj` 为：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClaudeCodeInstaller.Core\ClaudeCodeInstaller.Core.csproj" />
  </ItemGroup>
</Project>
```

注意：App 的 `csproj` 里要删掉模板自动加的 `<ProjectReference>`（若有重复，保留上述这一个）。

- [ ] **Step 4: 空的 Program.cs 与 MainForm.cs，保证可编译**

`src/ClaudeCodeInstaller.App/Program.cs`:

```csharp
namespace ClaudeCodeInstaller.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
```

`src/ClaudeCodeInstaller.App/MainForm.cs`（临时空壳，后续 Task 10 填完整 UI）：

```csharp
namespace ClaudeCodeInstaller.App;

public class MainForm : Form
{
    public MainForm()
    {
        Text = "Claude Code 一键安装器";
        Width = 520;
        Height = 640;
    }
}
```

- [ ] **Step 5: 构建 + 跑基线测试**

```bash
dotnet build -c Release
dotnet test tests/ClaudeCodeInstaller.Tests
```
Expected: build 成功（0 error）；xunit 模板自带 1 个测试通过。

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: scaffold solution with Core/App/Tests projects"
```

---

### Task 1: VersionInfo 常量

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/VersionInfo.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/VersionInfoTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/VersionInfoTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter VersionInfoTests`
Expected: FAIL（VersionInfo 不存在）。

- [ ] **Step 3: 实现常量**

`src/ClaudeCodeInstaller.Core/VersionInfo.cs`:

```csharp
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
    // 固定版本兜底：换版本时改这里 + 下面的资产名。真实资产命名格式为
    // `CC-Switch-{版本}-Windows.msi`（x64；arm64 用 -arm64.msi）。
    public const string CcSwitchPinnedTag = "v3.20.1";
    public const string CcSwitchPinnedAsset = "CC-Switch-v3.20.1-Windows.msi";
    public static readonly string[] CcSwitchMirrors =
    {
        "https://mirror.ghproxy.com/",
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
    };
    public static string PinnedCcSwitchUrl(string mirrorPrefix) =>
        $"{mirrorPrefix}https://github.com/{CcSwitchRepo}/releases/download/{CcSwitchPinnedTag}/{CcSwitchPinnedAsset}";
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter VersionInfoTests`
Expected: PASS（3 个测试全过）。

- [ ] **Step 5: 验证 Node zip 真实可用**

Run: `curl -sI https://npmmirror.com/mirrors/node/v24.16.0/node-v24.16.0-win-x64.zip | head -1`
Expected: `HTTP/2 200`。若 404，更新 `NodeVersion` 为一个存在的 LTS 版本并重跑测试。

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add version constants and mirror lists"
```

---

### Task 2: DownloadHelper（多源回退 + 流式进度）

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/DownloadHelper.cs`
- Create: `tests/ClaudeCodeInstaller.Tests/FakeHttpHandler.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/DownloadHelperTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/FakeHttpHandler.cs`:

```csharp
using System.Net;

namespace ClaudeCodeInstaller.Tests;

public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses;
    public FakeHttpHandler(params Func<HttpResponseMessage>[] responses) =>
        _responses = new Queue<Func<HttpResponseMessage>>(responses);
    public List<string> RequestedUrls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestedUrls.Add(request.RequestUri!.ToString());
        var next = _responses.Count > 0 ? _responses.Dequeue() : NotFound;
        return Task.FromResult(next());
    }

    public static HttpResponseMessage Ok(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}
```

`tests/ClaudeCodeInstaller.Tests/DownloadHelperTests.cs`:

```csharp
using ClaudeCodeInstaller.Core;
using Xunit;

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
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter DownloadHelperTests`
Expected: FAIL（DownloadHelper 不存在）。

- [ ] **Step 3: 实现 DownloadHelper**

`src/ClaudeCodeInstaller.Core/DownloadHelper.cs`:

```csharp
using System.Net;

namespace ClaudeCodeInstaller.Core;

public class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
}

public record DownloadProgress(long BytesReceived, long? TotalBytes, int Percent, string Source);

public interface IDownloadHelper
{
    Task<string> DownloadFirstAvailableAsync(IReadOnlyList<string> sources, string destDir,
        string fileName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}

public sealed class DownloadHelper : IDownloadHelper
{
    private const int AttemptsPerSource = 2;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _client;

    public DownloadHelper(HttpMessageHandler? handler = null)
    {
        var http = handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _client = new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeInstaller/1.0");
    }

    public async Task<string> DownloadFirstAvailableAsync(IReadOnlyList<string> sources, string destDir,
        string fileName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var source in sources)
        {
            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                try
                {
                    return await DownloadFromAsync(source, destDir, fileName, progress, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    errors.Add($"{source} 超时 (尝试 {attempt})");
                    progress?.Report(new DownloadProgress(0, null, 0, $"{source} 超时，重试…"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{source} (尝试 {attempt}): {ex.Message}");
                    progress?.Report(new DownloadProgress(0, null, 0, $"{source} 失败: {ex.Message}"));
                }
            }
        }
        throw new DownloadException("所有下载源均失败。\n" + string.Join("\n", errors));
    }

    private async Task<string> DownloadFromAsync(string url, string destDir, string fileName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var partPath = Path.Combine(destDir, fileName + ".part");
        var finalPath = Path.Combine(destDir, fileName);

        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(partPath);
        var buffer = new byte[81920];
        long received = 0;
        var lastReported = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if (total is { } t and > 0)
            {
                var pct = (int)(received * 100 / t);
                if (pct != lastReported)
                {
                    lastReported = pct;
                    progress?.Report(new DownloadProgress(received, t, pct, url));
                }
            }
            else
            {
                progress?.Report(new DownloadProgress(received, null, -1, url));
            }
        }
        File.Move(partPath, finalPath, overwrite: true);
        progress?.Report(new DownloadProgress(received, total, 100, url));
        return finalPath;
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter DownloadHelperTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add DownloadHelper with multi-source fallback"
```

---

### Task 3: ProcessRunner + PathManager

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/ProcessRunner.cs`
- Create: `src/ClaudeCodeInstaller.Core/PathManager.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/ProcessRunnerTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/ProcessRunnerTests.cs`:

```csharp
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task ExitCodeZero_ReturnsSuccess()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "exit 0" });
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task ExitCodeNonZero_IsPropagated()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "exit 3" });
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task CapturesStandardOutput()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("cmd.exe", new[] { "/c", "echo hello" });
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public void AppendUserPath_AddsDirOnce()
    {
        var manager = new PathManager();
        var before = manager.GetUserPath();
        try
        {
            manager.AppendUserPath("C:\\fake\\node");
            manager.AppendUserPath("C:\\fake\\node");
            var after = manager.GetUserPath();
            Assert.Contains("C:\\fake\\node", after);
            Assert.Equal(before + ";C:\\fake\\node", after);
        }
        finally
        {
            // 还原测试污染的用户 PATH
            Environment.SetEnvironmentVariable("Path", before, EnvironmentVariableTarget.User);
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter "ProcessRunnerTests|AppendUserPath"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现 ProcessRunner**

`src/ClaudeCodeInstaller.Core/ProcessRunner.cs`:

```csharp
using System.Diagnostics;

namespace ClaudeCodeInstaller.Core;

public record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool Cancelled);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
        string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
        string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, output, ct);
        var stderrTask = ReadLinesAsync(process.StandardError, output, ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr, ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { /* reads cancelled; ignore */ }
            throw;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* already exited or access denied — best effort */ }
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, IProgress<string>? output, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            sb.AppendLine(line);
            output?.Report(line);
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: 实现 PathManager**

`src/ClaudeCodeInstaller.Core/PathManager.cs`:

```csharp
namespace ClaudeCodeInstaller.Core;

public interface IPathManager
{
    string GetUserPath();
    void AppendUserPath(string dir);
}

public sealed class PathManager : IPathManager
{
    public string GetUserPath() =>
        Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";

    public void AppendUserPath(string dir)
    {
        var current = GetUserPath();
        var normalized = dir.TrimEnd(Path.DirectorySeparatorChar);
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(e => e.TrimEnd(Path.DirectorySeparatorChar).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return;
        var updated = string.IsNullOrEmpty(current) ? normalized : current.TrimEnd(';') + ";" + normalized;
        Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
    }
}
```

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter "ProcessRunnerTests|AppendUserPath"`
Expected: PASS。

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add ProcessRunner and PathManager"
```

---

### Task 4: ConfigWriter（settings.json 合并 + 备份）

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/ConfigWriter.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/ConfigWriterTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/ConfigWriterTests.cs`:

```csharp
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
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter ConfigWriterTests`
Expected: FAIL。

- [ ] **Step 3: 实现 ConfigWriter**

`src/ClaudeCodeInstaller.Core/ConfigWriter.cs`:

```csharp
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
            var text = await File.ReadAllTextAsync(settingsPath, ct);
            if (JsonNode.Parse(text) is JsonObject parsed) root = parsed;
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
        await File.CopyAsync(path, backup, ct);
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
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter ConfigWriterTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: ConfigWriter merges settings.json and seeds cc-switch"
```

---

### Task 5: NodeInstaller（便携 zip，免管理员）

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/NodeInstaller.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/NodeInstallerTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/NodeInstallerTests.cs`:

```csharp
using System.IO.Compression;
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class NodeInstallerTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int WhereExitCode { get; set; } = 1; // `where node` 默认找不到
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
        {
            Calls.Add((fileName, args));
            var isWhere = args.Contains("node") && fileName.EndsWith("where.exe");
            var exit = isWhere ? WhereExitCode : 0;
            return Task.FromResult(new ProcessResult(exit, isWhere && WhereExitCode == 0 ? "C:\\fake\\node\\node.exe" : "", "", false));
        }
    }

    private sealed class FakePathManager : IPathManager
    {
        public string Path { get; private set; } = "";
        public void AppendUserPath(string dir) => Path += (Path == "" ? "" : ";") + dir;
    }

    private static string CreateNodeZip(string dir)
    {
        var zipPath = Path.Combine(dir, "node.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        archive.CreateEntry("node.exe").Open().Close();
        archive.CreateEntry("npm.cmd").Open().Close();
        return zipPath;
    }

    private sealed class FakeDownloader : IDownloadHelper
    {
        private readonly string _zipPath;
        public FakeDownloader(string zipPath) => _zipPath = zipPath;
        public Task<string> DownloadFirstAvailableAsync(IReadOnlyList<string> sources, string destDir,
            string fileName, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var dest = Path.Combine(destDir, fileName);
            File.Copy(_zipPath, dest);
            return Task.FromResult(dest);
        }
    }

    [Fact]
    public async Task NodeMissing_InstallsPortableNode_AndAppendsPath()
    {
        var profile = Path.Combine(Path.GetTempPath(), "node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var zip = CreateNodeZip(profile);
        try
        {
            var fakeProc = new FakeProcessRunner { WhereExitCode = 1 };
            var fakePath = new FakePathManager();
            var installer = new NodeInstaller(new FakeDownloader(zip), fakeProc, fakePath);

            var result = await installer.EnsureNodeAsync(profile, null, CancellationToken.None);

            Assert.False(result.AlreadyInstalled);
            var nodeDir = Path.Combine(profile, ".nodejs");
            Assert.True(File.Exists(Path.Combine(nodeDir, "node.exe")));
            Assert.True(File.Exists(Path.Combine(nodeDir, "npm.cmd")));
            Assert.Equal(Path.Combine(nodeDir, "npm.cmd"), result.NpmCmd);
            Assert.Contains(nodeDir, fakePath.Path);
        }
        finally { Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task NodeAlreadyInstalled_SkipsInstall()
    {
        var profile = Path.Combine(Path.GetTempPath(), "node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            var fakeProc = new FakeProcessRunner { WhereExitCode = 0 };
            var installer = new NodeInstaller(new FakeDownloader(profile + ".zip"), fakeProc, new FakePathManager());

            var result = await installer.EnsureNodeAsync(profile, null, CancellationToken.None);

            Assert.True(result.AlreadyInstalled);
            Assert.Equal("C:\\fake\\node\\node.exe", result.NpmCmd); // 复用已存在 node 的 npm（同目录假设）
        }
        finally { Directory.Delete(profile, true); }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter NodeInstallerTests`
Expected: FAIL。

- [ ] **Step 3: 实现 NodeInstaller**

`src/ClaudeCodeInstaller.Core/NodeInstaller.cs`:

```csharp
using System.IO.Compression;

namespace ClaudeCodeInstaller.Core;

public record NodeInstallResult(bool AlreadyInstalled, string NodeDir, string NpmCmd);

public interface INodeInstaller
{
    Task<NodeInstallResult> EnsureNodeAsync(string userProfileDir, IProgress<string>? log, CancellationToken ct);
}

public sealed class NodeInstaller : INodeInstaller
{
    private readonly IDownloadHelper _downloader;
    private readonly IProcessRunner _runner;
    private readonly IPathManager _pathManager;

    public NodeInstaller(IDownloadHelper downloader, IProcessRunner runner, IPathManager pathManager)
    {
        _downloader = downloader;
        _runner = runner;
        _pathManager = pathManager;
    }

    public async Task<NodeInstallResult> EnsureNodeAsync(string userProfileDir, IProgress<string>? log, CancellationToken ct)
    {
        var nodeDir = Path.Combine(userProfileDir, ".nodejs");
        var ownNode = Path.Combine(nodeDir, "node.exe");
        var ownNpm = Path.Combine(nodeDir, "npm.cmd");
        if (File.Exists(ownNode) && File.Exists(ownNpm))
        {
            log?.Report("检测到已安装的便携 Node，跳过安装。");
            return new NodeInstallResult(true, nodeDir, ownNpm);
        }

        // 检查系统其它位置的 node（含 `where node`）。注意：必须确认 npm.cmd 同目录存在，
        // 否则返回的 NpmCmd 会让后续 npm 步骤失败——npm.cmd 缺失时改装便携版。
        var where = await _runner.RunAsync("where.exe", new[] { "node" }, null, null, ct);
        if (where.ExitCode == 0 && !string.IsNullOrWhiteSpace(where.StandardOutput))
        {
            var found = where.StandardOutput.Trim().Split('\n')[0].Trim();
            var foundDir = Path.GetDirectoryName(found)!;
            var foundNpm = Path.Combine(foundDir, "npm.cmd");
            if (File.Exists(foundNpm))
            {
                log?.Report($"检测到已有 Node: {found}，跳过安装。");
                return new NodeInstallResult(true, foundDir, foundNpm);
            }
            log?.Report($"检测到 Node 但缺少 npm.cmd，改装便携版。");
        }

        log?.Report($"未检测到 Node，正在下载 Node {VersionInfo.NodeVersion}…");
        var tmp = Path.Combine(Path.GetTempPath(), "claude-code-installer");
        var zipPath = await _downloader.DownloadFirstAvailableAsync(
            VersionInfo.NodeZipSources, tmp, VersionInfo.NodeZipFileName, null, ct);

        log?.Report("正在解压 Node…");
        Directory.CreateDirectory(nodeDir);
        ZipFile.ExtractToDirectory(zipPath, nodeDir, overwriteFiles: true);

        // zip 顶层是 "node-vXX-win-x64/"，把内容上移一层
        var inner = Directory.GetDirectories(nodeDir).SingleOrDefault();
        if (inner is not null)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
            {
                var target = Path.Combine(nodeDir, Path.GetFileName(entry));
                if (Directory.Exists(entry) && !Directory.Exists(target))
                    Directory.Move(entry, target);
                else if (File.Exists(entry) && !File.Exists(target))
                    File.Move(entry, target);
            }
            Directory.Delete(inner, recursive: true);
        }

        ct.ThrowIfCancellationRequested();
        _pathManager.AppendUserPath(nodeDir);
        log?.Report("Node 安装完成，已加入用户 PATH。");
        return new NodeInstallResult(false, nodeDir, Path.Combine(nodeDir, "npm.cmd"));
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter NodeInstallerTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: portable Node.js installer without admin"
```

---

### Task 6: ClaudeInstaller（npm 镜像安装）

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/ClaudeInstaller.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/ClaudeInstallerTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/ClaudeInstallerTests.cs`:

```csharp
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class ClaudeInstallerTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public bool ClaudeExists { get; set; }
        public Queue<int> NpmExitCodes { get; } = new();
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
        {
            Calls.Add((fileName, args));
            if (args.Contains("claude") && fileName.EndsWith("where.exe"))
            {
                return Task.FromResult(new ProcessResult(ClaudeExists ? 0 : 1, ClaudeExists ? "C:\\Users\\u\\AppData\\Roaming\\npm\\claude.cmd" : "", "", false));
            }
            var exit = NpmExitCodes.Count > 0 ? NpmExitCodes.Dequeue() : 0;
            return Task.FromResult(new ProcessResult(exit, "", exit == 0 ? "" : "npm error", false));
        }
    }

    [Fact]
    public async Task InstallsViaNpmMirrorRegistry()
    {
        var fake = new FakeRunner();
        var installer = new ClaudeInstaller(fake);

        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);

        var npmCall = fake.Calls.Single(c => c.FileName == "C:\\nodejs\\npm.cmd");
        Assert.Contains("install", npmCall.Args);
        Assert.Contains("@anthropic-ai/claude-code", npmCall.Args);
        Assert.Contains("--registry=https://registry.npmmirror.com", npmCall.Args);
        Assert.False(result.AlreadyInstalled);
    }

    [Fact]
    public async Task FirstRegistryFails_RetriesWithFallback()
    {
        var fake = new FakeRunner();
        fake.NpmExitCodes.Enqueue(1); // 镜像 registry 失败
        fake.NpmExitCodes.Enqueue(0); // 官方 registry 成功
        var installer = new ClaudeInstaller(fake);

        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);

        var npmCalls = fake.Calls.Where(c => c.FileName == "C:\\nodejs\\npm.cmd").ToList();
        Assert.Equal(2, npmCalls.Count);
        Assert.Contains("--registry=https://registry.npmjs.org", npmCalls[1].Args);
    }

    [Fact]
    public async Task ClaudeAlreadyInstalled_IsUpgraded()
    {
        var fake = new FakeRunner { ClaudeExists = true };
        var installer = new ClaudeInstaller(fake);

        var result = await installer.EnsureClaudeAsync("C:\\nodejs\\npm.cmd", null, CancellationToken.None);

        Assert.True(result.AlreadyInstalled);
        Assert.Single(fake.Calls.Where(c => c.FileName == "C:\\nodejs\\npm.cmd"));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter ClaudeInstallerTests`
Expected: FAIL。

- [ ] **Step 3: 实现 ClaudeInstaller**

`src/ClaudeCodeInstaller.Core/ClaudeInstaller.cs`:

```csharp
namespace ClaudeCodeInstaller.Core;

public record ClaudeInstallResult(bool AlreadyInstalled, string ClaudeCmd);

public interface IClaudeInstaller
{
    Task<ClaudeInstallResult> EnsureClaudeAsync(string npmCmd, IProgress<string>? log, CancellationToken ct);
}

public sealed class ClaudeInstaller : IClaudeInstaller
{
    private readonly IProcessRunner _runner;

    public ClaudeInstaller(IProcessRunner runner) => _runner = runner;

    public async Task<ClaudeInstallResult> EnsureClaudeAsync(string npmCmd, IProgress<string>? log, CancellationToken ct)
    {
        var whereFound = await FindClaudePathAsync(ct);
        var alreadyInstalled = whereFound is not null;

        Exception? lastError = null;
        foreach (var registry in VersionInfo.NpmRegistries)
        {
            try
            {
                log?.Report($"正在安装 Claude CLI（registry: {registry}）…");
                var result = await _runner.RunAsync(npmCmd, new[]
                {
                    "install", "-g", VersionInfo.ClaudePackage,
                    "--registry=" + registry, "--no-fund", "--no-audit",
                }, null, log, ct);
                if (result.ExitCode == 0)
                {
                    log?.Report("Claude CLI 安装完成。");
                    // 权威路径：npm 实际装到哪就用哪（便携 Node 的全局前缀不是 %AppData%\npm）。
                    // 安装后重新 `where claude`，其次取安装前 found，最后回退默认 AppData 路径。
                    var finalPath = await FindClaudePathAsync(ct) ?? whereFound ?? DefaultClaudeCmd();
                    return new ClaudeInstallResult(alreadyInstalled, finalPath);
                }
                lastError = new InvalidOperationException($"npm 退出码 {result.ExitCode}: {result.StandardError}");
            }
            catch (Exception ex) { lastError = ex; }
        }
        throw lastError ?? new InvalidOperationException("npm 安装失败。");
    }

    private async Task<string?> FindClaudePathAsync(CancellationToken ct)
    {
        var where = await _runner.RunAsync("where.exe", new[] { "claude" }, null, null, ct);
        if (where.ExitCode == 0 && !string.IsNullOrWhiteSpace(where.StandardOutput))
            return where.StandardOutput.Trim().Split('\n')[0].Trim();
        return null;
    }

    private static string DefaultClaudeCmd() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter ClaudeInstallerTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Claude CLI install via npm with mirror fallback"
```

---

### Task 7: CcSwitchInstaller（GitHub 最新版 + 镜像）

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/CcSwitchInstaller.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/CcSwitchInstallerTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/CcSwitchInstallerTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class CcSwitchInstallerTests
{
    private const string FakeReleaseJson = """
    {
      "tag_name": "v0.3.1",
      "assets": [
        { "name": "cc-switch_0.3.1_macos_x64.dmg", "browser_download_url": "https://x/dmg" },
        { "name": "cc-switch_0.3.1_x64-setup.exe", "browser_download_url": "https://github.com/farion1231/cc-switch/releases/download/v0.3.1/cc-switch_0.3.1_x64-setup.exe" }
      ]
    }
    """;

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = new();
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? workingDirectory = null, IProgress<string>? output = null, CancellationToken ct = default)
        {
            Calls.Add((fileName, args));
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }
    }

    [Fact]
    public async Task ResolvesLatestAsset_AndInstallsSilently()
    {
        var handler = new FakeHttpHandler(() => FakeHttpHandler.Ok(Encoding.UTF8.GetBytes(FakeReleaseJson)));
        var runner = new FakeRunner();
        var installer = new CcSwitchInstaller(new DownloadHelper(handler), runner);

        var result = await installer.EnsureCcSwitchAsync(null, CancellationToken.None);

        Assert.True(result.Installed);
        var install = runner.Calls.Single();
        Assert.EndsWith("-setup.exe", install.FileName);
        Assert.Equal("/S", install.Args.Single());
        Assert.Contains("api.github.com", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task ApiFails_FallsBackToPinnedMirrorUrl()
    {
        var handler = new FakeHttpHandler(FakeHttpHandler.NotFound(), FakeHttpHandler.NotFound());
        var runner = new FakeRunner();
        var installer = new CcSwitchInstaller(new DownloadHelper(handler), runner);

        var result = await installer.EnsureCcSwitchAsync(null, CancellationToken.None);

        Assert.True(result.Installed);
        Assert.Contains("mirror.ghproxy.com", handler.RequestedUrls.Skip(1).First());
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter CcSwitchInstallerTests`
Expected: FAIL。

- [ ] **Step 3: 实现 CcSwitchInstaller**

`src/ClaudeCodeInstaller.Core/CcSwitchInstaller.cs`:

```csharp
using System.Text.Json.Nodes;

namespace ClaudeCodeInstaller.Core;

public record CcSwitchInstallResult(bool Installed, string InstallerPath);

public interface ICcSwitchInstaller
{
    Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct);
}

public sealed class CcSwitchInstaller : ICcSwitchInstaller
{
    private readonly IDownloadHelper _downloader;
    private readonly IProcessRunner _runner;

    public CcSwitchInstaller(IDownloadHelper downloader, IProcessRunner runner)
    {
        _downloader = downloader;
        _runner = runner;
    }

    public async Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "claude-code-installer");

        // 策略 1：GitHub API 解析最新版资产 URL
        var assetUrl = await TryResolveLatestFromApiAsync(log, ct);
        if (assetUrl is not null)
        {
            var fileName = Path.GetFileName(new Uri(assetUrl).AbsolutePath);
            try
            {
                return await DownloadAndInstallAsync(new[] { assetUrl }, tmp, fileName, log, ct);
            }
            catch (DownloadException ex)
            {
                // 最新版资产下载失败也要回退镜像，不能直接抛错
                log?.Report($"最新版下载失败，改用固定版本镜像回退: {ex.Message}");
            }
        }
        else
        {
            log?.Report("GitHub API 不可用，改用固定版本镜像下载。");
        }

        // 策略 2：固定版本经镜像下载（回退）
        var sources = VersionInfo.CcSwitchMirrors.Select(VersionInfo.PinnedCcSwitchUrl).ToList();
        try
        {
            return await DownloadAndInstallAsync(sources, tmp, VersionInfo.CcSwitchPinnedAsset, log, ct);
        }
        catch (DownloadException ex)
        {
            throw new InvalidOperationException("cc-switch 下载失败（已尝试所有镜像）。\n" + ex.Message);
        }
    }

    private async Task<string?> TryResolveLatestFromApiAsync(IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeInstaller/1.0");
            var json = await client.GetStringAsync(VersionInfo.CcSwitchApiUrl, ct);
            var root = JsonNode.Parse(json) as JsonObject;
            var assets = root?["assets"] as JsonArray;
            var asset = assets?.FirstOrDefault(a =>
            {
                var name = a?["name"]?.GetValue<string>();
                return name is not null
                    && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("arm64", StringComparison.OrdinalIgnoreCase);
            });
            var url = asset?["browser_download_url"]?.GetValue<string>();
            if (url is not null) log?.Report($"已解析 cc-switch 最新版资产: {url}");
            return url;
        }
        catch (Exception ex)
        {
            log?.Report($"解析 cc-switch 最新版失败: {ex.Message}");
            return null;
        }
    }

    private async Task<CcSwitchInstallResult> DownloadAndInstallAsync(IReadOnlyList<string> sources,
        string tmp, string fileName, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"正在下载 cc-switch…");
        var path = await _downloader.DownloadFirstAvailableAsync(sources, tmp, fileName, null, ct);

        if (fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            log?.Report("正在静默安装 cc-switch (MSI)…");
            var r = await _runner.RunAsync("msiexec.exe", new[] { "/i", path, "/qn", "/norestart" }, null, log, ct);
            if (r.ExitCode != 0) throw new InvalidOperationException($"msiexec 退出码 {r.ExitCode}");
        }
        else
        {
            log?.Report("正在静默安装 cc-switch (NSIS)…");
            var r = await _runner.RunAsync(path, new[] { "/S" }, null, log, ct);
            if (r.ExitCode != 0) throw new InvalidOperationException($"安装程序退出码 {r.ExitCode}");
        }
        return new CcSwitchInstallResult(true, path);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter CcSwitchInstallerTests`
Expected: PASS。

- [ ] **Step 5: 真实环境验证资产名**

Run:
```bash
curl -s https://api.github.com/repos/farion1231/cc-switch/releases/latest | grep -o '"name": "[^"]*"' | head
```
Expected: 看到 Windows 的 `.exe`/`.msi` 资产名。若与 `CcSwitchPinnedAsset`/`CcSwitchPinnedTag` 不符，更新 `VersionInfo` 常量并重跑测试。

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: cc-switch latest-release resolver with mirror fallback"
```

---

### Task 8: InstallationEngine（步骤编排 + 事件）

**Files:**
- Create: `src/ClaudeCodeInstaller.Core/InstallationEngine.cs`
- Test: `tests/ClaudeCodeInstaller.Tests/InstallationEngineTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/ClaudeCodeInstaller.Tests/InstallationEngineTests.cs`:

```csharp
using ClaudeCodeInstaller.Core;
using Xunit;

namespace ClaudeCodeInstaller.Tests;

public class InstallationEngineTests
{
    private sealed class FailingNode : INodeInstaller
    {
        public Task<NodeInstallResult> EnsureNodeAsync(string userProfileDir, IProgress<string>? log, CancellationToken ct) =>
            Task.FromResult(new NodeInstallResult(true, "C:\\nodejs", "C:\\nodejs\\npm.cmd"));
    }

    private sealed class FakeClaude : IClaudeInstaller
    {
        public bool Throw { get; set; }
        public Task<ClaudeInstallResult> EnsureClaudeAsync(string npmCmd, IProgress<string>? log, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException("npm 网络失败");
            return Task.FromResult(new ClaudeInstallResult(false, "C:\\npm\\claude.cmd"));
        }
    }

    private sealed class FakeCcSwitch : ICcSwitchInstaller
    {
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public Task<CcSwitchInstallResult> EnsureCcSwitchAsync(IProgress<string>? log, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("cc-switch 下载失败");
            return Task.FromResult(new CcSwitchInstallResult(true, "C:\\cc.exe"));
        }
    }

    private sealed class FakeConfig : IConfigWriter
    {
        public Task<ConfigWriteResult> WriteDeepSeekConfigAsync(string userProfileDir, string apiKey,
            string model, CancellationToken ct = default) =>
            Task.FromResult(new ConfigWriteResult(null, true, null, true, "ok"));
    }

    private static InstallationEngine BuildEngine(FakeClaude? claude = null, FakeCcSwitch? cc = null) =>
        new(new FailingNode(), claude ?? new FakeClaude(), cc ?? new FakeCcSwitch(),
            new FakeConfig(), Path.GetTempPath());

    [Fact]
    public async Task AllStepsRunInOrder_Success()
    {
        var engine = BuildEngine();
        var steps = new List<InstallStepId>();
        engine.StepStarted += (s, _) => steps.Add(s);
        engine.Finished += (_, success) => Assert.True(success);

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "deepseek-v4-flash", InstallCcSwitch = true });

        Assert.Equal(
            new[] { InstallStepId.Node, InstallStepId.Claude, InstallStepId.CcSwitch, InstallStepId.Config, InstallStepId.Verify },
            steps);
    }

    [Fact]
    public async Task CcSwitchFailure_DoesNotBlockCore()
    {
        var cc = new FakeCcSwitch { Throw = true };
        var engine = BuildEngine(cc: cc);
        var success = false;
        engine.Finished += (_, s) => success = s;

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = true });

        Assert.True(success); // claude + 配置仍成功
        Assert.Equal(1, cc.Calls);
    }

    [Fact]
    public async Task ClaudeFailure_AbortsWithError()
    {
        var claude = new FakeClaude { Throw = true };
        var engine = BuildEngine(claude: claude);
        var success = true;
        string? message = null;
        engine.Finished += (msg, s) => { success = s; message = msg; };

        await engine.RunAsync(new InstallOptions { ApiKey = "sk-x", Model = "m", InstallCcSwitch = false });

        Assert.False(success);
        Assert.Contains("npm 网络失败", message);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter InstallationEngineTests`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现 InstallationEngine**

`src/ClaudeCodeInstaller.Core/InstallationEngine.cs`:

```csharp
namespace ClaudeCodeInstaller.Core;

public class InstallOptions
{
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public bool InstallCcSwitch { get; init; } = true;
}

public enum InstallStepId { Node, Claude, CcSwitch, Config, Verify }

public sealed class InstallationEngine
{
    private readonly INodeInstaller _node;
    private readonly IClaudeInstaller _claude;
    private readonly ICcSwitchInstaller _ccSwitch;
    private readonly IConfigWriter _config;
    private readonly string _userProfileDir;
    private readonly IProcessRunner _verifyRunner;

    public event Action<InstallStepId, string>? StepStarted;
    public event Action<string>? Log;
    public event Action<int>? Progress;
    public event Action<string, bool>? Finished;

    public InstallationEngine(INodeInstaller node, IClaudeInstaller claude, ICcSwitchInstaller ccSwitch,
        IConfigWriter config, string userProfileDir, IProcessRunner? verifyRunner = null)
    {
        _node = node;
        _claude = claude;
        _ccSwitch = ccSwitch;
        _config = config;
        _userProfileDir = userProfileDir;
        _verifyRunner = verifyRunner ?? new ProcessRunner();
    }

    public async Task RunAsync(InstallOptions options, CancellationToken ct)
    {
        var log = new Progress<string>(s => Log?.Invoke(s));
        var ccSwitchMessage = "已跳过 cc-switch。";
        try
        {
            // 1. Node（0–30）
            StepStarted?.Invoke(InstallStepId.Node, "检查 / 安装 Node.js");
            Progress?.Invoke(5);
            var node = await _node.EnsureNodeAsync(_userProfileDir, log, ct);

            // 2. Claude（30–50）
            StepStarted?.Invoke(InstallStepId.Claude, "安装 Claude CLI");
            Progress?.Invoke(35);
            var claude = await _claude.EnsureClaudeAsync(node.NpmCmd, log, ct);

            // 3. cc-switch（50–70），可选，失败不阻塞
            if (options.InstallCcSwitch)
            {
                StepStarted?.Invoke(InstallStepId.CcSwitch, "安装 cc-switch");
                Progress?.Invoke(55);
                try
                {
                    await _ccSwitch.EnsureCcSwitchAsync(log, ct);
                    ccSwitchMessage = "已安装 cc-switch。";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ccSwitchMessage = "cc-switch 安装失败（已跳过，不影响使用）: " + ex.Message;
                    Log?.Invoke(ccSwitchMessage);
                }
            }

            // 4. 写配置（70–90）
            StepStarted?.Invoke(InstallStepId.Config, "写入 DeepSeek 配置");
            Progress?.Invoke(75);
            var cfg = await _config.WriteDeepSeekConfigAsync(_userProfileDir, options.ApiKey, options.Model, ct);
            if (cfg.SettingsBackupPath is not null)
                Log?.Invoke($"已备份原 settings.json → {cfg.SettingsBackupPath}");
            if (!cfg.CcSwitchSeeded)
                Log?.Invoke(cfg.CcSwitchSeedMessage);

            // 5. 验证（90–100）
            StepStarted?.Invoke(InstallStepId.Verify, "验证安装");
            Progress?.Invoke(95);
            var claudeVersion = await VerifyAsync(claude.ClaudeCmd, ct);

            Progress?.Invoke(100);
            var summary = string.Join("\n",
                "安装完成 ✔",
                $"Claude CLI: {claude.ClaudeCmd}",
                string.IsNullOrEmpty(claudeVersion) ? "版本检查未通过" : $"版本: {claudeVersion}",
                ccSwitchMessage);
            Finished?.Invoke(summary, true);
        }
        catch (OperationCanceledException)
        {
            Finished?.Invoke("已取消安装。", false);
        }
        catch (Exception ex)
        {
            Finished?.Invoke("安装失败：" + ex.Message, false);
        }
    }

    private async Task<string> VerifyAsync(string claudeCmd, CancellationToken ct)
    {
        try
        {
            var r = await _verifyRunner.RunAsync(claudeCmd, new[] { "--version" }, null, null, ct);
            return r.ExitCode == 0 ? r.StandardOutput.Trim() : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/ClaudeCodeInstaller.Tests --filter InstallationEngineTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: installation engine orchestrating all steps"
```

---

### Task 9: MainForm 完整 UI

**Files:**
- Modify: `src/ClaudeCodeInstaller.App/MainForm.cs`

- [ ] **Step 1: 实现完整 UI（代码构建，无 designer）**

替换 `src/ClaudeCodeInstaller.App/MainForm.cs` 为：

```csharp
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeInstaller.Core;

namespace ClaudeCodeInstaller.App;

public class MainForm : Form
{
    private readonly TextBox _apiKeyBox = new() { UseSystemPasswordChar = true, PlaceholderText = "sk-..." };
    private readonly ComboBox _modelBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,   // 可手输
    };
    private readonly CheckBox _ccSwitchCheck = new() { Text = "安装 cc-switch 切换工具", Checked = true };
    private readonly Button _testButton = new() { Text = "测试连接" };
    private readonly Button _startButton = new() { Text = "▶ 开始安装", Enabled = false };
    private readonly ProgressBar _progressBar = new() { Minimum = 0, Maximum = 100 };
    private readonly RichTextBox _logBox = new() { ReadOnly = true, BackColor = Color.FromArgb(20, 20, 28), ForeColor = Color.LightGray };
    private readonly Button _launchButton = new() { Text = "启动 Claude Code", Enabled = false };
    private readonly Button _closeButton = new() { Text = "关闭", DialogResult = DialogResult.Cancel };
    private readonly Label _progressLabel = new() { Text = "" };
    private InstallationEngine? _engine;
    private bool _installing;
    private CancellationTokenSource? _cts;
    private bool _closeAfterCancel;

    public MainForm()
    {
        Text = "Claude Code 一键安装器";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(540, 660);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;

        _modelBox.Items.AddRange(new object[] { "deepseek-v4-flash", "deepseek-chat", "deepseek-reasoner" });
        _modelBox.Text = VersionInfo.DefaultModel;
        _apiKeyBox.TextChanged += (_, _) => _startButton.Enabled = !_installing && _apiKeyBox.Text.Trim().Length > 0;
        _testButton.Click += async (_, _) => await TestConnectionAsync();
        _startButton.Click += async (_, _) => await StartInstallAsync();

        BuildLayout();
        _launchButton.Click += LaunchClaude;
        FormClosing += OnFormClosing;
        Log("请填写 DeepSeek API Key 并选择模型，然后点击「开始安装」。");
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_installing) return;
        e.Cancel = true;                 // 安装中阻止关窗，先取消
        _closeAfterCancel = true;        // 安装结束后再关闭
        _cts?.Cancel();
        Log("正在取消安装…");
    }

    private void BuildLayout()
    {
        var y = 24;
        AddRow("DeepSeek API Key", _apiKeyBox, ref y);
        AddRow("模型名称", _modelBox, ref y);

        _ccSwitchCheck.Location = new Point(130, y); _ccSwitchCheck.Width = 320; y += 40;
        Controls.Add(_ccSwitchCheck);

        _testButton.Location = new Point(130, y); _testButton.Width = 120; _testButton.Height = 34;
        _startButton.Location = new Point(280, y); _startButton.Width = 170; _startButton.Height = 34;
        Controls.Add(_testButton); Controls.Add(_startButton);
        y += 60;

        _progressLabel.Location = new Point(24, y); _progressLabel.Size = new Size(490, 22); y += 26;
        Controls.Add(_progressLabel);

        _progressBar.Location = new Point(24, y); _progressBar.Size = new Size(490, 22); y += 40;
        Controls.Add(_progressBar);

        _logBox.Location = new Point(24, y); _logBox.Size = new Size(490, 300); y += 320;
        Controls.Add(_logBox);

        _launchButton.Location = new Point(24, y); _launchButton.Width = 150; _launchButton.Height = 36;
        _closeButton.Location = new Point(190, y); _closeButton.Width = 100; _closeButton.Height = 36;
        Controls.Add(_launchButton); Controls.Add(_closeButton);
    }

    private void AddRow(string label, Control input, ref int y)
    {
        Controls.Add(new Label { Text = label, Location = new Point(24, y + 6), Width = 100 });
        input.Location = new Point(130, y);
        input.Width = 360;
        Controls.Add(input);
        y += 46;
    }

    private void Log(string line)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
    }

    private async Task TestConnectionAsync()
    {
        var key = _apiKeyBox.Text.Trim();
        if (key.Length == 0) { MessageBox.Show("请先填写 API Key。"); return; }
        SetBusy(true);
        Log("正在测试连接…");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var resp = await client.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log("✘ API Key 无效（401）。"); MessageBox.Show("API Key 无效，请检查后重试。", "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var list = (JsonNode.Parse(json)?["data"] as JsonArray)?
                .Select(m => m?["id"]?.GetValue<string>()).Where(x => x is not null).ToList();
            var modelOk = list is not null && list.Contains(_modelBox.Text.Trim());
            Log(modelOk ? $"✔ 连接成功，模型 {_modelBox.Text} 存在。" : $"⚠ 连接成功，但模型「{_modelBox.Text}」不在列表中（可能仍可用，或需换名）。");
            MessageBox.Show(modelOk ? "连接成功 ✔" : "连接成功，但模型名需确认。", "测试结果");
        }
        catch (Exception ex)
        {
            Log("✘ 连接失败: " + ex.Message);
            MessageBox.Show("连接失败：" + ex.Message, "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task StartInstallAsync()
    {
        if (_installing) return;
        _installing = true;
        SetBusy(true);
        _launchButton.Enabled = false;
        _logBox.Clear();
        _progressBar.Value = 0;
        _progressLabel.Text = "";

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _engine = new InstallationEngine(new NodeInstaller(new DownloadHelper(), new ProcessRunner(), new PathManager()),
            new ClaudeInstaller(new ProcessRunner()),
            new CcSwitchInstaller(new DownloadHelper(), new ProcessRunner()),
            new ConfigWriter(), profile);
        _cts = new CancellationTokenSource();
        _engine.Log += Log;
        _engine.Progress += p => _progressBar.Value = p;
        _engine.StepStarted += (step, desc) =>
        {
            _progressLabel.Text = desc;
            Log($"── {desc}");
        };
        _engine.Finished += (message, success) =>
        {
            Log(success ? "==== 完成 ====" : "==== 失败 ====");
            foreach (var line in message.Split('\n')) Log(line);
            _installing = false;
            SetBusy(false);
            _launchButton.Enabled = success;
            if (success) MessageBox.Show(message, "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else MessageBox.Show(message, "安装未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _cts?.Dispose();
            _cts = null;
            if (_closeAfterCancel)
            {
                _closeAfterCancel = false;
                Close();          // 此刻 _installing 已为 false，FormClosing 不会再拦截
            }
        };

        await _engine.RunAsync(new InstallOptions
        {
            ApiKey = _apiKeyBox.Text.Trim(),
            Model = _modelBox.Text.Trim(),
            InstallCcSwitch = _ccSwitchCheck.Checked,
        }, _cts.Token);
    }

    private void SetBusy(bool busy)
    {
        _testButton.Enabled = !busy;
        _startButton.Enabled = !busy && _apiKeyBox.Text.Trim().Length > 0;
        _apiKeyBox.Enabled = !busy;
        _modelBox.Enabled = !busy;
        _ccSwitchCheck.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void LaunchClaude(object? sender, EventArgs e)
    {
        var claudeCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        if (!File.Exists(claudeCmd)) claudeCmd = "claude";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{claudeCmd}\"") { UseShellExecute = true });
    }
}
```
- [ ] **Step 2: 构建 + 手工冒烟运行**

```bash
dotnet build -c Release
dotnet run --project src/ClaudeCodeInstaller.App
```
Expected: 窗口弹出，布局正常；不填 Key 时「开始安装」置灰；填 Key 可点「测试连接」。

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: full installer UI"
```

---

### Task 10: build.ps1 + README

**Files:**
- Create: `build.ps1`
- Create: `README.md`

- [ ] **Step 1: 编写 build.ps1**

`build.ps1`:

```powershell
$ErrorActionPreference = 'Stop'
Write-Host "Publishing ClaudeCodeInstaller (win-x64 self-contained single file)..."
dotnet publish src/ClaudeCodeInstaller.App -c Release -o dist
Write-Host "Done: dist\ClaudeCodeInstaller.App.exe -> 重命名为 ClaudeCodeInstaller.exe 分发"
```

- [ ] **Step 2: 编写 README.md**

`README.md`:

```markdown
# Claude Code 一键安装器

给 DeepSeek 用户的一键安装工具：填 Key、选模型、点开始，自动完成
Node.js → Claude CLI → cc-switch → DeepSeek 配置，全程免管理员。

## 使用
1. 运行 `ClaudeCodeInstaller.exe`（首次运行点「更多信息 → 仍要运行」——未签名属正常）。
2. 填 DeepSeek API Key，选模型（默认 `deepseek-v4-flash`），点「测试连接」可先验证。
3. 点「开始安装」，等待完成。
4. 点「启动 Claude Code」进入终端使用。

## 工作原理
- Node.js 便携版解压到 `%USERPROFILE%\.nodejs`，加入用户 PATH（免管理员）
- Claude CLI 经 npmmirror 安装
- 配置写入 `%USERPROFILE%\.claude\settings.json`（原文件自动备份 `.bak-时间戳`）
- cc-switch 从 GitHub 最新版下载（自动镜像回退），预置 DeepSeek provider

## 开发者
- 构建：`powershell -File build.ps1`
- 测试：`dotnet test`
- 版本常量：`src/ClaudeCodeInstaller.Core/VersionInfo.cs`（Node 版本 / cc-switch 固定版本）

## 常见问题
- **SmartScreen 提示**：未签名 exe 正常现象，点「更多信息 → 仍要运行」。
- **cc-switch 装失败**：不影响核心使用（claude + DeepSeek 已配置好）。
- **模型报错**：换成 `deepseek-chat` 再试；DeepSeek 兼容端点模型名以官方为准。
```

- [ ] **Step 3: 发布验证**

```bash
powershell -File build.ps1
ls -la dist/
```
Expected: `dist/ClaudeCodeInstaller.App.exe` 单文件产出（约 60–100MB，含压缩）。重命名为 `ClaudeCodeInstaller.exe` 后在干净机器测试。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: add build script and README"
```

---

### Task 11: 端到端验收

**Files:** 无（手工验收）

- [ ] **Step 1: 干净环境全流程**

在**无 Node** 的 Windows 10/11 机器（或 VM）上：
1. 运行 exe → 填真实 DeepSeek Key → 开始安装
2. 观察日志：Node 下载（npmmirror）→ npm 装 claude → cc-switch 下载安装 → 配置写入 → 版本验证
3. 完成提示 → 点「启动 Claude Code」

- [ ] **Step 2: 功能验证**

在 claude 终端里：
```
/status
```
Expected: 显示 `ANTHROPIC_BASE_URL` 为 DeepSeek 端点、模型为所选模型；实际对话有回复。

- [ ] **Step 3: 回归验证配置安全**

1. 先用文本编辑器在 `~/.claude/settings.json` 里放一个自定义键（如 `"MY_KEY":"keep"`）
2. 再次运行安装器（不同 Key）
3. 确认：自定义键保留、DeepSeek 键被更新、备份文件 `.bak-*` 生成

- [ ] **Step 4: 断网/半墙验证**

1. 关闭外网重跑：Node 下载应给出「所有下载源均失败」及区分性错误
2. 用工具模拟 GitHub 被墙但 npmmirror 正常：claude/Node 链路应正常，cc-switch 走镜像或失败可跳过

- [ ] **Step 5: cc-switch schema 复核**

安装器装好 cc-switch 后打开其 GUI，确认「DeepSeek」provider 出现在列表且可切换。若 schema 与预置不符，修正 `ConfigWriter.SeedCcSwitchAsync` 的结构，更新 Task 4 测试后重新提交。

---

## Self-Review（编写计划时的自查结果）

- **Spec 覆盖**：界面/输入/测试连接（Task 9）；安装流程五步（Task 5/6/7/8）；settings.json 合并与备份（Task 4）；cc-switch 可选+预置（Task 4/7/8）；多镜像回退（Task 2 + VersionInfo）；构建交付（Task 10）；测试计划（Task 11）。全部有对应任务。
- **占位符扫描**：无 TBD/TODO。`<用户输入的 Key>` 等在规格中是动态值标记，计划已用真实变量 `options.ApiKey` 落实。
- **类型一致性**：接口名、方法签名在 File Structure 与各 Task 间一致（`IDownloadHelper.DownloadFirstAvailableAsync`、`EnsureNodeAsync(userProfileDir,...)`、`ConfigWriteResult` 字段等均已交叉核对）。
- **已知偏差已记录**：Node 便携 zip（免管理员）、cc-switch schema 尽力而为（Task 11 Step 5 复核兜底）。
