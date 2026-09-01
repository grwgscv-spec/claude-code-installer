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
