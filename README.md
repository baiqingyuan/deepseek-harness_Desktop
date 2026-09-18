<p align="center">
  <img src="assets/cover.png" width="100%" alt="DeepSeek Harness Desktop">
</p>

<h1 align="center">DeepSeek Harness Desktop</h1>

<p align="center">
  <b>Windows 桌面版 DeepSeek Harness</b> —— 自包含 exe，双击即用，无需安装 Node / 浏览器 / 命令行。
</p>

<p align="center">
  <a href="https://github.com/baiqingyuan/deepseek-harness_Desktop/releases"><img src="https://img.shields.io/github/v/release/baiqingyuan/deepseek-harness_Desktop" alt="GitHub Release"></a>
  <a href="https://github.com/baiqingyuan/deepseek-harness_Desktop/releases"><img src="https://img.shields.io/github/downloads/baiqingyuan/deepseek-harness_Desktop/total" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6" alt="Platform">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/baiqingyuan/deepseek-harness_Desktop" alt="License"></a>
</p>

> 底层是官方开源的 [DeepSeek Harness (dsh)](https://github.com/deepseek-ai/DeepSeek-Harness)（MIT），本项目为它提供一个原生 Windows 桌面壳（C# WinForms + WebView2）。

---

## ✨ 特性

- ✅ **真正独立的 exe**：原生窗口（无地址栏/标签页），基于 WebView2
- ✅ **自包含**：应用目录自带 `node.exe` + 全部 `node_modules`，整个文件夹可整体拷走
- ✅ **打开即用**：双击启动 → 自动拉起本地 dsh 服务 → 窗口加载界面（实测约 4 秒就绪）
- ✅ **关窗不停服**：关闭窗口只是最小化到系统托盘，本地服务继续运行；右键托盘图标「真正退出」才会停止（退出时清理整棵进程树，不留残留）
- ✅ **无终端黑框**：本地服务以无控制台方式启动（v0.7.x 每次启动都会闪出一个 node 终端窗口）
- ✅ **简洁的 Windows 扁平界面**：自绘标题栏（左侧图标与标题 / 右侧本地端口 / 标准最小化·最大化·关闭按钮，悬停有反馈）+ 1px 描边与投影，配色为中性灰白 + DeepSeek 蓝
- ✅ **高 DPI 清晰不发虚**：声明 `PerMonitorV2` 高 DPI 感知，125%/150% 缩放下按设备像素渲染（此前会被系统位图拉伸导致整个界面模糊）
- ✅ **零配置**：Windows 10/11 自带 .NET Framework 与 WebView2 运行时，无需额外安装
- ✅ **自动修复依赖**：检测到 WebView2 运行时缺失时会弹窗引导一键下载并静默安装；node / dsh 文件被杀软误删或被占用端口时给出明确中文提示，不再黑话报错
- ✅ **跟随上游**：自动适配新版 dsh 的浏览器鉴权（解析一次性 token URL），不会打开系统默认浏览器、界面只在本窗口内
- ✅ **内置更新**：启动后静默检查最新版（读取随 Release 发布的静态清单 `latest.json`，**不受 GitHub API 限流影响**）。有新版本时主界面**左下角设置按钮右侧**会出现「新版本 vX.Y」按钮，点击即下载（按钮上显示百分比），再点「安装并重启」→ 自动关闭应用、**静默**覆盖安装并重新打开，全程没有安装向导；也可右键托盘 → **「检查更新…」**

## 📥 下载

从 [Releases 页面](https://github.com/baiqingyuan/deepseek-harness_Desktop/releases) 下载最新版：

- `DeepSeekHarness-Setup-vX.Y.Z-win-x64.exe`（**推荐**：双击按向导安装，自动创建桌面/开始菜单快捷方式，可在系统设置中一键卸载，无需管理员权限）
- `DeepSeekHarness-Desktop-vX.Y.Z-win-x64.zip`（便携版，约 110 MB，解压即用）

## 🚀 使用

1. **推荐**：下载 `DeepSeekHarness-Setup-vX.Y.Z-win-x64.exe`，双击按向导安装（默认装到用户目录，无需管理员权限），桌面与开始菜单自动出现 `DeepSeek Harness` 快捷方式；卸载在「设置 → 应用」里一键完成。
2. 或下载便携版 zip → 解压（整个文件夹一起解压）→ 双击 `install.bat` 一键创建快捷方式，也可直接双击 `DeepSeekHarness.exe` 运行。
3. 首次打开在界面中配置你的 **DeepSeek API Key**
4. 关闭窗口 = 最小化到系统托盘（服务继续运行）；**单击托盘图标**恢复窗口，右键「真正退出」才彻底停止
5. **升级**：右键托盘 →「检查更新…」→ 按提示下载安装即可；有新版本时启动也会托盘提示一次（点气泡即可升级）
6. 便携版如需卸载：运行 `uninstall.bat` 删除快捷方式，再删除整个文件夹即可

**系统要求**：Windows 10/11 x64（内置 .NET Framework 4.8 与 WebView2 运行时）

## 🛠 从源码构建

```powershell
# 需要：Windows + PowerShell + Node.js（联网；pnpm 缺失时脚本会自动安装）
./build.ps1            # 产物在 dist\DeepSeekHarness\
./package-release.ps1  # 若 dist 不存在会自动先 build；压缩包在 dist\DeepSeekHarness-Desktop-vX.Y.Z-win-x64.zip
```

构建脚本会自动：获取 Node.js → 用 pnpm 安装 `@deepseek-ai/dsh`（扁平布局，无符号链接；pnpm 缺失时自动 `corepack` / `npm i -g pnpm`）→ 下载 WebView2 程序集 → 用系统 csc 编译 exe。

内置版本可通过参数覆盖（默认值见 `build.ps1` 的 `param` 块）：

```powershell
./build.ps1 -DshVersion latest   # 或 alpha / 具体版本号，如 0.1.6-alpha.2
./build.ps1 -NodeVersion v24.21.0
```

## 🔄 与上游 / 官方桌面版

- 上游仓库：[deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)，CLI 包为 [`@deepseek-ai/dsh`](https://www.npmjs.com/package/@deepseek-ai/dsh)。本项目默认锁定其 npm `latest` 通道。
- 上游已另有一个 **Electron 桌面版**（仓库内 `apps/desktop`，含自动更新与 Windows 安装包）。本项目定位是**极薄的 WinForms + WebView2 原生壳**：无 Electron 运行时、体积更小、随官方 npm 包一键升级；更新走轻量的「应用内检查 + 下载安装包覆盖」，而不是内置自动更新框架。两者可并存，按需选择。
- 上游若发布破坏性变更（例如 Web 控制台鉴权方式变化），本项目的适配点集中在 `src/App.cs` 的服务启动与就绪检测部分。

也可以直接用 GitHub Actions 一键发布（无需本机环境）：

- **推 `v*` tag**：自动构建并上传到 Release（见 `.github/workflows/release.yml`）。
- **手动触发**：在 Actions 页面选 `Build and Release` → `Run workflow`，填入版本号（如 `1.0.0`）即可，无需打 tag。

## 📁 目录结构

```
src/App.cs          桌面壳源码（C# WinForms + WebView2）
src/Version.cs      版本号（由 build.ps1 编译时注入，用于「检查更新」）
minimum-version.txt 可选：一行版本号，低于它的客户端被要求强制升级（详见常见问题）
src/BUILD.md        手工编译说明
build.ps1           一键构建（含 NSIS 安装包编译，缺失时自动下载 NSIS）
package-release.ps1 打包 zip
installer.nsi       NSIS 安装包脚本（由 build.ps1 调用，生成 Setup.exe）
assets/cover.png    仓库封面
icons/              应用图标
dist/               构建产物（不入库）
```

## ❓ 常见问题

**Q：为什么杀毒软件/SmartScreen 有提示？**
未签名的自包含 exe 首次运行可能触发 SmartScreen，点"更多信息 → 仍要运行"即可。长期使用可自行用代码签名证书签名。

**Q：老版本怎么升级？**
v0.4.0 及更早**没有内置更新入口**，需要手动下载一次 v0.5.0 安装包覆盖安装（安装程序会自动结束正在运行的应用，装到同一目录，配置与会话不会丢失）。装好之后，后续版本就能直接用托盘「检查更新…」一键升级。便携版（zip）用户请手动下载新 zip 解压覆盖。

**Q：更新提示从来没出现过，是不是坏了？**
v0.6.0 起更新检查优先读随 Release 发布的静态清单 `latest.json`（走 release 资源下载域名，**没有 GitHub API 的匿名限流**）。v0.5.0 走的是 `api.github.com`，匿名限流 **60 次/小时/IP** —— 同一公司/校园出口下多人使用时可能被限流，且失败是静默的（只是不提示）。升级到 v0.6.0 即可根治。

**Q：怎么强制老用户升级（停用旧版本）？**
在仓库根放一个 `minimum-version.txt`，内容写一行版本号（如 `0.6.0`），然后发版。低于该版本的客户端每次启动都会弹窗要求升级（而不是只在托盘静默提示）。删掉该文件即解除限制。

**Q：更新会丢配置吗？**
不会。安装包覆盖的是程序文件，API Key 与会话数据存在用户目录（`%LOCALAPPDATA%` / dsh 的 home 目录），不会被安装程序删除。

**Q：需要装 Node.js 吗？**
不需要。发布包自带 `node.exe` 与全部依赖。

**Q：可以拷到别的电脑用吗？**
可以，整个 `DeepSeekHarness` 文件夹一起拷贝即可（保持 exe 与 node.exe/node_modules 同目录）。

**Q：WebView2 是什么？**
Windows 10/11 自带的 Edge 内核运行时；若系统精简版缺失，应用启动时会**自动弹出引导，一键下载并静默安装**，无需手动操作。也可从 [微软官网](https://developer.microsoft.com/microsoft-edge/webview2/) 手动安装。

## ⚠️ 免责声明

- 本项目为社区封装，与 DeepSeek 官方无隶属关系；底层 Harness 版权归 DeepSeek AI（MIT）。
- 使用需遵守 DeepSeek API 服务条款。
- 第三方组件（Node.js、WebView2、各 npm 包）的许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 📄 License

MIT，详见 [LICENSE](LICENSE)。

---

## English

**DeepSeek Harness Desktop** is a portable Windows app for [DeepSeek Harness (dsh)](https://github.com/deepseek-ai/DeepSeek-Harness): a standalone `DeepSeekHarness.exe` (C# WinForms + WebView2) that bundles its own Node.js runtime and dependencies.

- ✅ No Node.js / browser / CLI needed
- ✅ Double-click to run; the local dsh service auto-starts, and closing the window only minimizes to tray (choose "Quit" in the tray menu to stop it)
- ✅ Portable folder — copy it anywhere on Windows 10/11 x64
- ⬇️ Download from [Releases](https://github.com/baiqingyuan/deepseek-harness_Desktop/releases)

> This is a community wrapper, not affiliated with DeepSeek. The underlying Harness is MIT-licensed by DeepSeek AI.
