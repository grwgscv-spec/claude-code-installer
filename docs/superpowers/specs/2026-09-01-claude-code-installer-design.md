# Claude Code 一键安装器 — 设计文档

- **日期**: 2026-09-01
- **状态**: 已确认（brainstorming 完成，待转入实现计划）

## 1. 目标

构建一个可分发给他人的 Windows 一键安装工具（单 exe）：

- 窗口输入 DeepSeek API Key，模型名可选（默认 `deepseek-v4-flash`）
- 点击安装后自动：安装 Node.js → 安装 Claude CLI → 下载安装 cc-switch → 写入 DeepSeek 配置
- 用户只等待下载/安装完成，全程有进度与日志反馈

## 2. 目标用户与约束

- **分发给别人**（朋友/社群），按可分发成品标准做
- **国内网络为主**：claude.ai / GitHub 直连不可靠，必须内置多镜像 + 自动回退 + 重试
- 目标机器：Windows 10+，**无 Node 环境**是常态
- 安装方式：**npm + 顺带装 Node**（npmmirror 国内镜像最稳）

## 3. 技术选型

- **exe**: C# WinForms + .NET 9，`dotnet publish` 出**自包含单文件** exe（win-x64，免运行时依赖）
- 图标：暂用默认图标（后续可换）
- cc-switch：**可选安装**（默认勾选、可取消）；核心功能不依赖它

## 4. 架构

```
MainForm (WinForms UI)
  · 输入 API Key / 模型 / 选项
  · 进度条 + 滚动日志框
  ├─ InstallationEngine   步骤编排（后台线程 Task.Run）
  │    通过事件回调更新 UI（进度/日志）
  ├─ DownloadHelper       多镜像源 + 自动回退重试 + 流式进度
  ├─ NodeInstaller        检测/静默安装 Node.js
  ├─ ClaudeInstaller      npm 安装 @anthropic-ai/claude-code
  ├─ CcSwitchInstaller    下载 + 静默安装 cc-switch
  └─ ConfigWriter         合并写入 ~/.claude/settings.json + 预置 cc-switch 配置
```

- 后台任务在 `Task.Run` 中运行，UI 通过事件/`Invoke` 更新，不阻塞窗口
- 单元边界：每个 Installer 只负责一件事，通过事件对外报告进度与错误

## 5. 界面布局

单窗口约 520×640：

```
┌──────────────────────────────────────────────┐
│  🚀 Claude Code 一键安装器                    │
│  一键配置 DeepSeek，免去所有手动步骤           │
├──────────────────────────────────────────────┤
│  DeepSeek API Key   [••••••••••••••••]       │
│  模型名称           [deepseek-v4-flash  ▾]   │  ← 可下拉也可手输
│  ☑ 安装 cc-switch 切换工具（默认勾选）        │
├──────────────────────────────────────────────┤
│  [ 测试连接 ]        [ ▶ 开始安装 ]           │
├──────────────────────────────────────────────┤
│  进度  ▓▓▓▓▓▓▓░░░░  42%                      │
│  [滚动日志区：每步状态 / 下载进度 / 错误]     │
│  ──────────────────────────────              │
│  [ 启动 Claude Code ]   [ 关闭 ]             │  ← 完成后亮起
└──────────────────────────────────────────────┘
```

- API Key：密码框（遮罩）
- 模型名：可编辑下拉框，预设 `deepseek-v4-flash`（默认）、`deepseek-chat`、`deepseek-reasoner`
- 「测试连接」：安装前先调 DeepSeek API 验证 key + 模型名有效
- 完成后「启动 Claude Code」亮起，用完整路径拉起新终端

## 6. 安装流程

| # | 步骤 | 说明 |
|---|------|------|
| 0 | 预检 | 检测已装 Node / Claude / cc-switch；读取现有 `~/.claude/settings.json` 并自动备份 |
| 1 | 装 Node.js | 未检测到时：从 npmmirror 下载指定 LTS 版 `node-vXX-win-x64.msi`，`msiexec /qn` 静默安装 |
| 2 | 装 Claude CLI | `npm install -g @anthropic-ai/claude-code`，registry 用 `https://registry.npmmirror.com`；已装旧版则升级 |
| 3 | 装 cc-switch | 勾选时：GitHub Releases 最新版 `.msi` 经国内镜像下载，静默安装 |
| 4 | 写配置 | 合并写 settings.json env 块 + 预置 cc-switch config.json |
| 5 | 验证 | `claude --version` 确认 → 亮起「启动 Claude Code」 |

### 6.1 写入 settings.json（合并，不覆盖原配置）

```json
{
  "env": {
    "ANTHROPIC_BASE_URL": "https://api.deepseek.com/anthropic",
    "ANTHROPIC_AUTH_TOKEN": "<用户输入的 Key>",
    "ANTHROPIC_MODEL": "<用户选择的模型>",
    "ANTHROPIC_SMALL_FAST_MODEL": "<同一模型>"
  }
}
```

背景小模型变量指向同一模型，避免 DeepSeek 不认识 haiku 模型导致后台任务报错。

### 6.2 预置 cc-switch provider

```json
{ "name": "DeepSeek", "baseUrl": "https://api.deepseek.com/anthropic",
  "apiKey": "<key>", "model": "<模型>" }
```

写入 `~/.cc-switch/config.json` 并设为当前 provider。

## 7. 下载策略与镜像回退

| 目标 | 首选源 | 回退源 |
|------|--------|--------|
| Node msi | `npmmirror.com/mirrors/node/` | `nodejs.org/dist/` |
| Claude CLI | `registry.npmmirror.com` | `registry.npmjs.org` |
| cc-switch msi | GitHub Releases 最新版 | ghproxy 系加速镜像依次回退 |

- 流式下载到临时文件，`Content-Length` 已知时显示真实百分比
- 每源重试 2 次（超时/连接失败才换源）；中断可从失败处重试
- 下载完校验：大小 > 0；MSI 能静默安装视为成功（可选 SHA256）
- 错误区分并明示：完全没网 / 源挂了 / 被墙，分别给不同提示

## 8. 容错

- 每步失败 → 红字日志 + 弹窗：「重试」/「跳过该步」（cc-switch 可跳过）/「中止」
- 重试从失败步骤续跑（记录已完成步骤）
- **配置写入在最后一步**：前面失败不碰 settings.json，无半配置状态
- 取消/失败清理临时文件；cc-switch 失败不影响核心功能

## 9. 构建与交付

项目结构（独立 git 仓库 `claude-code-installer/`）：

```
claude-code-installer/
  ├─ ClaudeCodeInstaller.sln
  ├─ src/ClaudeCodeInstaller/
  │    ├─ MainForm.cs / InstallationEngine.cs
  │    ├─ DownloadHelper.cs / ConfigWriter.cs / ...
  ├─ build.ps1
  └─ README.md
```

发布命令：

```
dotnet publish src/ClaudeCodeInstaller -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -o dist/
```

- 产物：单个 `ClaudeCodeInstaller.exe`（win-x64，Windows 10+ 免依赖）
- Node 版本号、cc-switch 版本号做成常量，更新时改常量重跑 `build.ps1`
- README 写明 SmartScreen 提示处理（未签名：「更多信息 → 仍要运行」）
- 后续可选：GitHub Actions 自动构建 + Release

## 10. 测试计划

1. 无 Node 干净 VM 跑全流程
2. 已装 Claude / 已有 settings.json 场景（验证备份与不覆盖）
3. 断网 / 半墙网络模拟，验证镜像回退与错误提示
4. 配置后 `claude` 实际能对话 DeepSeek

## 11. 实现时待验证的点

- Node 装完后刷新当前进程 PATH（读注册表）
- cc-switch `.msi` 静默参数依其打包器而定（Tauri 常见 NSIS/WiX），按实际包处理
- 「启动 Claude Code」用 `%APPDATA%\npm\claude.cmd` 完整路径拉起新终端
- cc-switch 配置文件实际路径与格式（~/.cc-switch/config.json，实现时以最新版确认为准）

## 12. 范围外（YAGNI）

- 不做断点续传（.part），只做「失败可重试」
- 不做多语言界面
- 不做自动更新/签名（后续可选）
- 不内置多个 API 服务商配置（用户以后用 cc-switch 自行添加）
