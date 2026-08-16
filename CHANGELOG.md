# Changelog

本项目所有重要改动记录于此。格式参考 [Keep a Changelog](https://keepachangelog.com/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 变更 (Changed)
- **托盘恢复改为单击**：原需双击托盘图标或右键「显示主界面」才能恢复窗口，现改为**单击托盘图标**即可恢复，更顺手；右键菜单仍保留「显示主界面 / 真正退出」。

## [0.3.0] - 2026-08-16

> 托盘常驻：关闭窗口不再直接停服务，而是最小化到系统托盘继续运行，避免误关后重等启动；并提供「真正退出」菜单彻底关闭。

> **已发布**：[Release v0.3.0](https://github.com/baiqingyuan/deepseek-harness-Desktop--/releases/tag/v0.3.0) 包含 zip（便携版）与 `Setup.exe`（NSIS 安装包）两个产物，由 CI 自动构建发布（`release.yml` 打 `v0.3.0` tag 触发）。

### 新增 (Added)
- **托盘常驻（最小化到托盘）**：点击窗口关闭按钮不再直接终止 dsh 本地服务，而是最小化到系统托盘并保持服务运行；双击托盘图标或右键「显示主界面」即可恢复窗口。首次最小化会弹出气泡提示说明。
- **「真正退出」菜单**：托盘右键菜单新增「真正退出」项，点击后隐藏托盘图标并彻底清理 dsh 进程树再退出，区别于「最小化到托盘」。
- **单实例呼起兼容隐藏态**：当应用已最小化到托盘时，重复双击 exe 仍可将已隐藏窗口恢复到前台（原逻辑依赖窗口可见性，已兼容隐藏态）。

## [0.2.0] - 2026-08-16

> 面向「开箱即用、少部署、少求助」的一次集中打磨：修复了桌面壳的多处运行时缺陷，并大幅简化构建与发布流程。

> **已发布**：[Release v0.2.0](https://github.com/baiqingyuan/deepseek-harness-Desktop--/releases/tag/v0.2.0) 包含两个产物 —— `DeepSeekHarness-Desktop-v0.2.0-win-x64.zip`（便携版）与 `DeepSeekHarness-Setup-v0.2.0-win-x64.exe`（NSIS 安装包）。CI 已构建验证（`build.ps1` 第 6 步 NSIS 定位修复见 `bbb2599`）。

### 修复 (Fixed)
- **启动期界面卡死（白屏）**：原 `StartServerIfNeeded()` 在 UI 线程同步 `Thread.Sleep` 等待 dsh 服务，最长冻结 90 秒。改为 `async/await + Task.Delay`，启动期间窗口可响应并显示「正在启动本地服务…（已等待 N 秒）」加载提示。
- **关窗残留子进程**：原 `Process.Kill()` 只杀 node 父进程，dsh 衍生的子进程会残留。新增 `KillProcessTree()`，通过 WMI 递归清理整棵进程树，真正做到「关窗即停」。
- **多实例互相误杀**：原逻辑在端口被占用时接管对方进程，关掉第二个实例会把第一个实例的服务也杀掉。新增单实例 `Mutex`，第二个实例仅把已运行窗口提到前台并退出，不再拉起/接管服务。

### 新增 (Added)
- **WebView2 运行时缺失 → 一键引导安装**：启动前自动探测 WebView2 运行时，缺失时弹窗询问「是否立即下载安装」，点「是」后自动静默安装并重新初始化，不再黑屏报错。
- **友好的缺失文件提示**：`node.exe` 或 dsh 文件被杀软误删时，提示文案改为中文并指明去 GitHub Releases 下载完整包。
- **端口占用明确提示**：端口 `3080` 被非本应用进程占用时，明确提示「端口被占用，请关闭占用程序后重试」，避免静默显示他人内容。
- **导航失败提示与错误落盘**：WebView2 初始化失败时弹一次友好提示；原始异常同时写入 `dsh-app-error.log` 便于排查。
- **一键安装脚本**：发布包内置 `install.bat` / `uninstall.bat`，双击即在桌面与开始菜单创建/移除 `DeepSeek Harness` 快捷方式，无需管理员权限（调用系统 `WScript.Shell` 生成 `.lnk`，零额外依赖）。
- **CI 手动一键发布**：`.github/workflows/release.yml` 新增 `workflow_dispatch` 的 `version` 输入，在 Actions 页面填版本号即可构建并发布，无需先打 tag。
- **NSIS 安装包（Setup.exe）**：构建流程新增 `installer.nsi` 与第 6 步，自动下载 NSIS 并编译出 `DeepSeekHarness-Setup-vX.Y.Z-win-x64.exe`。用户双击按向导安装到用户目录（无需管理员权限），自动创建桌面/开始菜单快捷方式并注册到「添加/删除程序」，可在系统设置中一键卸载；CI 同时上传 zip 与 Setup.exe 两种产物。

### 变更 (Changed)
- **构建自动化**：`build.ps1` 在未检测到 `pnpm` 时自动 `corepack enable` / `npm i -g pnpm` 兜底，省去「先装 pnpm」这一步。
- **打包自动化**：`package-release.ps1` 在 `dist` 缺失时自动先调用 `build.ps1`，实现「一条命令出包」。
- **文档更新**：`README.md` 补齐运行时自检、一键安装、一条命令构建/发布等说明；`src/BUILD.md` 修正过时的 `desktop\` 路径。
- **锁定依赖版本**：`build.ps1` 固定 `DshVersion = 0.1.0-rc.6`、`WebView2Version = 1.0.4129.50`、`node v24.14.0`，提升可复现性。

## [0.1.0] - 初始版本

- 首个可发布版本：基于 WinForms + WebView2 的桌面壳，封装官方 DeepSeek Harness（`@deepseek-ai/dsh`）。
- 自包含发布包：内置 Node.js 运行时与 `node_modules`，用户无需安装 Node/pnpm。
- 端口探测与异常残留接管清理；WebView2 E_ABORT 重试机制。
- 构建脚本：`build.ps1`、`package-release.ps1`、GitHub Actions 自动构建发布。

---

## Roadmap（下个版本规划，待确认）

> 以下为候选方向，按「用户价值 / 工作量」粗略排序，**非承诺清单**。实施前请确认优先级与上游 `@deepseek-ai/dsh` 的能力边界。

### 高优先（体验与可信度）
- **代码签名 / SmartScreen 免警告**：当前 `Setup.exe` 与 `exe` 无签名，Windows 会报「未知发布者」。引入签名（需代码签名证书，可用免费 `signtool` + 自签/受信 CA）消除拦截，显著提升普通用户安装成功率。
- **托盘常驻 + 最小化到托盘**：关闭窗口改为「最小化到系统托盘」而非直接停服务，点托盘图标恢复；避免误关后重等启动。可配套「真正退出」菜单项。 > **已在 v0.3.0 实现。**
- **升级 dsh 到稳定版**：`build.ps1` 固定 `DshVersion = 0.1.0-rc.6`（RC）。核查上游是否已发布正式版并跟进，减少 RC 不稳定带来的偶发问题。

### 中优先（自助与运维）
- **自动更新（Self-update）**：发布后用户无需手动回 Releases。新增「检查更新」+ 一键下载安装包（或静默调用 Setup.exe 覆盖安装）。
- **诊断面板**：把 `dsh-app-error.log` 做成可视化入口，支持「一键打开日志目录 / 导出」。
- **API Key 安全存储**：如 dsh 支持从环境变量或配置文件读取 Key，由 shell 预填并从 Windows 凭据管理器安全读取，避免每次重配。

### 低优先（扩展）
- **ARM64 构建**：当前仅 `win-x64`，增加 `win-arm64` 产物（需 NSIS arm64 与 node arm64 支持）。
- **卸载时彻底清理**：确保卸载流程杀净端口占用与残留进程。
- **中文本地化收尾**：界面/提示全中文化核查（部分点已在 v0.2.0 完成）。
