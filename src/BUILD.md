# 重新编译 DeepSeekHarness.exe

推荐直接用仓库根目录的一键脚本（自动准备 Node.js / WebView2 程序集 / dsh 依赖）：

```powershell
# 在仓库根目录执行（需要 Windows + PowerShell + 联网）
./build.ps1
```

产物在 `dist\DeepSeekHarness\`，包含 `DeepSeekHarness.exe`、`node.exe`、`node_modules\` 与 WebView2 三个 DLL。

---

若需手工编译（仅当改了 `src/App.cs` 而不想跑完整 build.ps1 时），环境要求：

- Windows 自带 .NET Framework 4.8 编译器：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- `src\Version.cs`：提供版本号常量 `AppInfo.Version`（「检查更新」据此与最新版比对）。手工编译时直接用仓库里的默认值即可，正式构建由 `build.ps1` 按 `-Version` 注入。
- WebView2 .NET 程序集：`Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.WinForms.dll`、`WebView2Loader.dll`
  - 运行 `build.ps1` 会在 `dist\_wv2` 下自动下载并解压这三个文件；也可从 NuGet 包 `Microsoft.Web.WebView2` 手动取 `lib\net462\` 与 `runtimes\win-x64\native\`。

```powershell
$root = "C:\path\to\deepseek-harness-Desktop--"
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$lib  = Join-Path $root "dist\_wv2"
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  "/win32icon:$root\icons\DeepSeekHarness.ico" `
  "/out:$root\dist\DeepSeekHarness\DeepSeekHarness.exe" `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
  "/r:$lib\Microsoft.Web.WebView2.Core.dll" `
  "/r:$lib\Microsoft.Web.WebView2.WinForms.dll" `
  "$root\src\App.cs" "$root\src\Version.cs"
if ($LASTEXITCODE -ne 0) { throw "csc 编译失败" }
```

运行要求（应用目录内需有）：

- DeepSeekHarness.exe
- node.exe（由 build.ps1 自动拷贝，或用 PATH 中的 Node.js）
- node_modules\@deepseek-ai\dsh\lib\bin.js
- Microsoft.Web.WebView2.Core.dll / .WinForms.dll / WebView2Loader.dll
- 系统装有 WebView2 运行时（Win10/11 默认已带；缺失时应用会引导一键安装）
