# Changelog

本项目所有重要改动记录于此。格式参考 [Keep a Changelog](https://keepachangelog.com/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.5.0] - 2026-09-18

> 补上「应用内更新」：托盘「检查更新…」可一键升级到 GitHub 最新版，启动时也会静默提示；同时修掉 CI 从未真正构建的历史问题。

### 新增 (Added)
- **应用内检查更新**：托盘右键 →「检查更新…」→ 查询 GitHub Releases 最新版 → 展示更新说明 → 下载安装包（带进度窗口）→ 自动关闭应用并启动安装程序。
- **启动静默检查**：界面就绪后查一次，有新版本时托盘气泡提示，点击气泡直接进入升级流程；无新版本完全无感，检查失败也静默处理。
- **托盘菜单新增**：「打开下载页面」（便携版手动下载）与「关于 / 版本 vX.Y.Z」（显示当前版本与本地服务端口）。

### 变更 (Changed)
- **版本号改为编译时注入**：新增 `src/Version.cs`（`AppInfo.Version`），`build.ps1` 按 `-Version` 写入后再编译，避免包内显示版本与实际发布版本不一致。
- **安装包支持覆盖升级**：NSIS 在 `.onInit` 里先结束正在运行的应用（含托盘常驻实例），避免 `DeepSeekHarness.exe` / `node.exe` 被占用导致升级失败。
- **Release 说明自动生成**：`release.yml` 新增步骤，从 `CHANGELOG.md` 提取当前版本章节作为 Release body（此前每个 Release 的说明都是空的）。

### 修复 (Fixed)
- **README 的 Releases 链接仓库名缺失**：链接与 badge 指向 `baiqingyuan/deepseek-harness`（少了 `_Desktop`），版本徽章与下载统计取不到数据，已全部修正。

> ⚠️ v0.4.0 及更早版本**没有更新入口**，需手动下载一次 v0.5.0 安装包覆盖安装（配置与会话不会丢失）；之后即可用托盘「检查更新…」一键升级。

## [0.4.0] - 2026-09-18

> 跟进上游 `deepseek-ai/deepseek-harness` 的近期更新，重点解决 **新版 dsh Web 控制台加了浏览器 token 鉴权** 导致的界面不可用问题（旧版壳直接访问 `http://127.0.0.1:3080` 会被 401 挡掉）。

### 修复 (Fixed)
- **适配 dsh 浏览器鉴权（关键）**：自 dsh `0.1.2` 起，Web 控制台要求浏览器会话鉴权 —— 每次启动生成一次性 token，`/?token=...` 经服务端校验后才下发会话 Cookie，未携带时所有 `/api` 调用返回 401，界面表现为能打开但连不上。桌面壳现在会捕获 dsh 的 stdout，解析 `dsh web: http://127.0.0.1:3080/?token=...` 就绪行并以此为起始地址；服务端校验后 303 跳转到干净根路径，界面恢复正常。旧版 dsh（无 token）打印的 URL 行同样适用。
- **不再额外弹系统浏览器**：启动时给 dsh 传 `--no-open`，界面只在本窗口内呈现（此前 dsh 会再开一次默认浏览器）。
- **残留进程改为重启而非接管**：端口被"本目录 dsh"的残留进程占用时，旧逻辑会接管它，但接管拿不到本次启动的 token，界面必然 401。现改为先清理该残留进程再重新拉起（判断为非本应用进程时仍按「端口被占用」报错）。
- **兜底兼容旧版 dsh**：若端口已开放但 10 秒内没有 URL 行（旧版不打印），直接访问根路径，不会卡满 90 秒。
- **CI 从未真正构建（严重）**：`release.yml` 在 `if:` 条件里直接引用了 `secrets` 上下文（8/26 接入 SignPath 时引入），GitHub 判定工作流文件非法（`Unrecognized named-value: 'secrets'`），表现为 **一个 job 都不执行、run 秒级 failure**。即那之后每次 push 的发布构建都是假失败。现改为在 job 级 `env` 先求值 `SIGNPATH_ENABLED`，步骤里再用 `env.SIGNPATH_ENABLED == 'true'` 判断。

### 新增 (Added)
- **端口自动回退**：`3080` 被其它程序占用时不再直接报错退出，而是自动换一个空闲端口并托盘提示一次，减少「打不开」这类求助。
- **服务意外退出提醒**：dsh 进程在运行中崩溃或被拦截时，托盘弹出一次警告，避免用户对着一个已经失效的界面发呆。

### 变更 (Changed)
- **升级底层 dsh 到 `0.1.5-rc.2`**（npm `latest` 稳定通道；`build.ps1` 的 `DshVersion` 支持填 `latest` / `alpha` 自动从 dist-tag 解析，官方 master 预发布线目前为 `0.1.6-alpha.2`）。
- **内置 Node.js 升级到 `v24.21.0`**（LTS "Krypton"；上游要求 `^22.19.0 || >=24.0.0`）。
- **退出改为优雅关闭**：官方 dsh 只在 `SIGINT` / `SIGTERM` 上做优雅关闭（5 秒内 dispose 插件树）。Windows 无信号，现在改为附加到子进程控制台后发送 Ctrl+C（node 在 Windows 上映射为 SIGINT），最多等 6 秒；仍未退出再补一记 Ctrl+Break，最后才退回原有的强制结束进程树。优雅路径不可用时行为与之前完全一致。
- **启动诊断直达**：dsh 启动失败时会把完整报告落到 `$DSH_HOME/logs/startup-*.log` 并在 stderr 给出 `Full diagnostics:` 行，现在该路径会直接显示在错误提示里。
- **构建缓存按版本失效**：`dist/_dsh-build` 增加版本戳，切换 dsh 版本时自动清理旧的 `node_modules`，避免装错版本。
- **pnpm 构建白名单对齐上游**：`koffi` / `node-pty` / `@deepseek-ai/dsh-subprocess-local` 允许，`@google/genai` / `protobufjs` / `node-addon-require-builtin` 显式拒绝；另加一次 `dangerouslyAllowAllBuilds` 兜底重试，减少升级 dsh 时的安装失败。
- **使用说明同步**：内置说明改为「关窗 = 最小化到托盘，服务继续运行；真正退出才停止」，并标注内置 dsh 版本。
- **托盘恢复改为单击**：原需双击托盘图标或右键「显示主界面」才能恢复窗口，现改为**单击托盘图标**即可恢复，更顺手；右键菜单仍保留「显示主界面 / 真正退出」。

### 安全 / 构建 (Security / Build)
- **CI 接入 SignPath 免费代码签名**：`release.yml` 在构建出 `Setup.exe` 后新增签名步骤（上传产物 → 提交 SignPath 签名 → 回写 `dist/`）。仅在仓库配置 `SIGNPATH_API_TOKEN` 等 Secrets 时生效，未配置则自动跳过、照常发布未签名包，不破坏现有流程。签名后 `Setup.exe` 由「未知发布者」升级为受 Windows 信任的 OV 证书（发布者显示为 SignPath Foundation）。

### 文档 (Docs)
- README 补充「与上游 / 官方桌面版」章节：说明上游已另有一个 Electron 桌面版（`apps/desktop`），本项目定位为更轻的 WinForms + WebView2 薄壳。

## [Unreleased]

_暂无。_

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
- ~~**代码签名 / SmartScreen 免警告**~~ > **已在 v0.4.0 通过 CI 接入 SignPath 实现。**
- ~~**托盘常驻 + 最小化到托盘**~~ > **已在 v0.3.0 实现。**
- ~~**升级 dsh 到稳定版**~~ > **已在 v0.4.0 跟进到 npm `latest`（0.1.5-rc.2）；上游仍为 0.1.x 预发布线，1.0 正式版发布后再跟进。**

### 中优先（自助与运维）
- **自动更新（Self-update）**：发布后用户无需手动回 Releases。新增「检查更新」+ 一键下载安装包（或静默调用 Setup.exe 覆盖安装）。
- **诊断面板**：把 `dsh-app-error.log` 做成可视化入口，支持「一键打开日志目录 / 导出」。
- **API Key 安全存储**：如 dsh 支持从环境变量或配置文件读取 Key，由 shell 预填并从 Windows 凭据管理器安全读取，避免每次重配。

### 低优先（扩展）
- **ARM64 构建**：当前仅 `win-x64`，增加 `win-arm64` 产物（需 NSIS arm64 与 node arm64 支持）。
- **卸载时彻底清理**：确保卸载流程杀净端口占用与残留进程。
- **中文本地化收尾**：界面/提示全中文化核查（部分点已在 v0.2.0 完成）。
