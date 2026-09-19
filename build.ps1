# build.ps1 - 一键构建 DeepSeek Harness Desktop 便携版
# 用法: ./build.ps1  （需要联网；Windows PowerShell / pwsh）
param(
    # dsh 版本：默认锁定官方 npm `latest`（稳定通道）。
    # 也可填 'latest' / 'alpha' 由 npm dist-tag 自动解析（alpha = 官方 master 上的预发布线）。
    [string]$DshVersion = "0.1.5-rc.2",
    [string]$WebView2Version = "1.0.4129.50",
    [string]$NodeVersion = "v24.21.0",
    [string]$Version = "0.8.19"
)
$ErrorActionPreference = 'Stop'

# 把 latest / alpha 这样的 dist-tag 解析成具体版本号后再锁定，保证可复现。
if ($DshVersion -eq 'latest' -or $DshVersion -eq 'alpha') {
    $tag = $DshVersion
    $resolved = $null
    try {
        $resolved = (& npm view "@deepseek-ai/dsh@$tag" version 2>$null | Select-Object -Last 1)
    } catch { }
    if ($resolved) { $resolved = ([string]$resolved).Trim() }
    if (-not $resolved -or $resolved -notmatch '^\d') {
        throw "无法从 npm 解析 @deepseek-ai/dsh@$tag 的版本号，请显式指定 -DshVersion（例如 0.1.5-rc.2）。"
    }
    $DshVersion = $resolved
    Write-Host "dsh dist-tag '$tag' -> $DshVersion"
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist\DeepSeekHarness"
$lib  = Join-Path $root "dist\_wv2"
$buildDir = Join-Path $root "dist\_dsh-build"
New-Item -ItemType Directory -Force -Path $dist, $lib, $buildDir | Out-Null

# ---------- 1. node.exe ----------
Write-Host "==> [1/6] node.exe"
$nodeCmd = Get-Command node -ErrorAction SilentlyContinue
if ($nodeCmd) {
    Copy-Item -LiteralPath $nodeCmd.Source -Destination (Join-Path $dist "node.exe") -Force
    Write-Host "    copied from PATH: $($nodeCmd.Source)"
} else {
    $nodeVer = $NodeVersion
    $tmpZip = Join-Path $env:TEMP "node-$nodeVer-win-x64.zip"
    $tmpDir = Join-Path $env:TEMP "node-$nodeVer-win-x64"
    if (-not (Test-Path (Join-Path $tmpDir "node.exe"))) {
        Write-Host "    downloading Node.js $nodeVer ..."
        Invoke-WebRequest -Uri "https://nodejs.org/dist/$nodeVer/node-$nodeVer-win-x64.zip" -OutFile $tmpZip
        Expand-Archive -LiteralPath $tmpZip -DestinationPath $tmpDir -Force
    }
    Copy-Item -LiteralPath (Join-Path $tmpDir "node.exe") -Destination (Join-Path $dist "node.exe") -Force
}

# ---------- 2. WebView2 程序集 ----------
Write-Host "==> [2/6] WebView2 assemblies"
$coreDll = Join-Path $lib "Microsoft.Web.WebView2.Core.dll"
if (-not (Test-Path $coreDll)) {
    $nupkg = Join-Path $lib "webview2.nupkg"
    Write-Host "    downloading Microsoft.Web.WebView2 $WebView2Version ..."
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$WebView2Version/microsoft.web.webview2.$WebView2Version.nupkg" -OutFile $nupkg
    $zip2 = Join-Path $lib "wv2.zip"
    Copy-Item -LiteralPath $nupkg -Destination $zip2 -Force
    Expand-Archive -LiteralPath $zip2 -DestinationPath (Join-Path $lib "extract") -Force
    Copy-Item -LiteralPath (Join-Path $lib "extract\lib\net462\Microsoft.Web.WebView2.Core.dll") -Destination $lib -Force
    Copy-Item -LiteralPath (Join-Path $lib "extract\lib\net462\Microsoft.Web.WebView2.WinForms.dll") -Destination $lib -Force
    Copy-Item -LiteralPath (Join-Path $lib "extract\runtimes\win-x64\native\WebView2Loader.dll") -Destination $lib -Force
}
Copy-Item -LiteralPath $coreDll -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $lib "Microsoft.Web.WebView2.WinForms.dll") -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $lib "WebView2Loader.dll") -Destination $dist -Force

# ---------- 3. dsh 依赖（扁平布局 node_modules） ----------
Write-Host "==> [3/6] dsh dependencies (node-linker=hoisted)"
$pkg = @{
    name = "dsh-build"; private = $true
    dependencies = @{ "@deepseek-ai/dsh" = $DshVersion }
}
$pkg | ConvertTo-Json -Depth 3 | Set-Content -Encoding ascii -Path (Join-Path $buildDir "package.json")
# 允许/拒绝哪些依赖的 install 脚本，与官方仓库 pnpm-workspace.yaml 保持一致：
# node-pty（ConPTY 持久 shell）与 koffi（Windows MoveFileExW 落盘）需要真实构建；
# 其余几个只是 no-op 脚本，显式拒绝以免 pnpm 严格模式报未登记错误。
@"
nodeLinker: hoisted
allowBuilds:
  '@deepseek-ai/dsh-subprocess-local': true
  koffi: true
  node-pty: true
  '@google/genai': false
  protobufjs: false
  node-addon-require-builtin: false
"@ | Set-Content -Encoding utf8 -Path (Join-Path $buildDir "pnpm-workspace.yaml")

# dsh 版本变了就必须重装：用版本戳文件判断缓存是否失效。
$stampFile = Join-Path $buildDir ".dsh-version"
$stamp = if (Test-Path $stampFile) { (Get-Content -LiteralPath $stampFile -Raw).Trim() } else { "" }
if ($stamp -ne $DshVersion -and (Test-Path (Join-Path $buildDir "node_modules"))) {
    Write-Host "    dsh 版本由 '$stamp' 变为 '$DshVersion'，清理旧依赖缓存 ..."
    Remove-Item -LiteralPath (Join-Path $buildDir "node_modules") -Recurse -Force -ErrorAction SilentlyContinue
}
Set-Content -Encoding ascii -Path $stampFile -Value $DshVersion

if (-not (Test-Path (Join-Path $buildDir "node_modules\@deepseek-ai\dsh\lib\bin.js"))) {
    # 未检测到 pnpm 时自动安装，省去开发者手动准备的步骤。
    $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
    if (-not $pnpm) {
        if (Get-Command corepack -ErrorAction SilentlyContinue) {
            Write-Host "    未检测到 pnpm，尝试 corepack enable ..."
            & corepack enable 2>$null
            & corepack prepare pnpm@latest --activate 2>$null
            $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
        }
        if (-not $pnpm -and (Get-Command npm -ErrorAction SilentlyContinue)) {
            Write-Host "    尝试 npm i -g pnpm ..."
            & npm i -g pnpm 2>$null
            $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
        }
    }
    if (-not $pnpm) { throw "未找到 pnpm，且自动安装失败。请先安装 pnpm（npm i -g pnpm 或 corepack enable）。" }
    Write-Host "    使用 pnpm: $($pnpm.Source)  安装 @deepseek-ai/dsh@$DshVersion"
    Push-Location $buildDir
    try { & $pnpm.Source install --no-frozen-lockfile }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) {
        # 新版 dsh 的闭包里可能新增了带 lifecycle 脚本的依赖，pnpm 严格模式会直接失败。
        # 这里放行一次所有构建脚本兜底，避免每次升级 dsh 都要手工补 allowBuilds。
        Write-Host "    严格构建白名单下安装失败，改用 dangerouslyAllowAllBuilds 重试 ..."
        Push-Location $buildDir
        try { & $pnpm.Source install --no-frozen-lockfile --config.dangerouslyAllowAllBuilds=true }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "pnpm install 失败" }
    }
}
if (Test-Path (Join-Path $dist "node_modules")) { Remove-Item -LiteralPath (Join-Path $dist "node_modules") -Recurse -Force }
Write-Host "    copying node_modules ..."
Copy-Item -LiteralPath (Join-Path $buildDir "node_modules") -Destination (Join-Path $dist "node_modules") -Recurse -Force

# ---------- 4. 编译 exe ----------
Write-Host "==> [4/6] compile DeepSeekHarness.exe"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = (Get-Command csc -ErrorAction SilentlyContinue).Source }
if (-not $csc) { throw "未找到 csc.exe（需要 .NET Framework 4.8，Windows 自带）" }

# 把版本号注入 src\Version.cs，供应用内「检查更新」与 GitHub 最新版比对
$versionCs = Join-Path $root "src\Version.cs"
@"
namespace DeepSeekHarness
{
    // 应用版本号，由 build.ps1 编译时按 -Version 注入，请勿手工修改。
    internal static class AppInfo
    {
        public const string Version = "$Version";
    }
}
"@ | Set-Content -Encoding utf8 -Path $versionCs

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    "/win32icon:$root\icons\DeepSeekHarness.ico" `
    "/win32manifest:$root\src\app.manifest" `
    "/out:$dist\DeepSeekHarness.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    "/r:$coreDll" `
    "/r:$lib\Microsoft.Web.WebView2.WinForms.dll" `
    "$root\src\App.cs" "$versionCs"
if ($LASTEXITCODE -ne 0) { throw "csc 编译失败" }
Copy-Item -LiteralPath (Join-Path $root "icons\DeepSeekHarness.ico") -Destination (Join-Path $dist "DeepSeekHarness.ico") -Force

# ---------- 5. 使用说明 ----------
Write-Host "==> [5/6] write 使用说明.txt"
@"
DeepSeek Harness 桌面版（内置 dsh $DshVersion）
========================
1. 双击 DeepSeekHarness.exe 即可使用
   （或双击「install.bat / 一键安装.bat」自动在桌面与开始菜单创建快捷方式）
2. 首次打开请在界面中配置 DeepSeek API Key
3. 关闭窗口 = 最小化到系统托盘，本地服务继续运行
   - 单击托盘图标恢复窗口；右键「真正退出」才会停止服务并退出
4. 界面由本窗口承载，不会额外打开系统浏览器

系统要求：Windows 10/11 x64（自带 .NET Framework 4.8 与 WebView2 运行时）。
请保持整个文件夹完整（node.exe / node_modules / DLL 与 exe 同目录），不要单独移动 exe。
如需卸载，运行 uninstall.bat 删除快捷方式，再删除本文件夹即可。
"@ | Set-Content -Encoding utf8 -Path (Join-Path $dist "使用说明.txt")

# ---------- 5b. 一键安装 / 卸载脚本 ----------
Write-Host "==> [5b] copy install.bat / uninstall.bat"
foreach ($f in @("install.bat", "uninstall.bat")) {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination $dist -Force }
}

# ---------- 6. NSIS 安装包 ----------
Write-Host "==> [6/6] NSIS installer (Setup.exe)"
$nsisDir = Join-Path $root "dist\_nsis"
$nsisExe = Join-Path $nsisDir "makensis.exe"
if (-not (Test-Path $nsisExe)) {
    # 优先从 PATH / 常见安装目录定位 makensis（覆盖 choco、官方安装器、用户自定义等场景）
    $candidates = @()
    $p = Get-Command makensis -ErrorAction SilentlyContinue
    if ($p) { $candidates += $p.Source }
    $candidates += @(
        (Join-Path $env:ProgramFiles "NSIS\makensis.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe"),
        "C:\Program Files\NSIS\makensis.exe",
        "C:\Program Files (x86)\NSIS\makensis.exe"
    )
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($found) {
        $nsisExe = $found
        Write-Host "    找到 makensis: $nsisExe"
    } else {
        Write-Host "    未检测到 makensis，下载 NSIS 便携版 ..."
        $nsisVer = "3.11"
        $nsisZip = Join-Path $env:TEMP "nsis-$nsisVer.zip"
        $urls = @(
            "https://downloads.sourceforge.net/project/nsis/NSIS%203/$nsisVer/nsis-$nsisVer.zip",
            "https://sourceforge.net/projects/nsis/files/NSIS%203/$nsisVer/nsis-$nsisVer.zip/download"
        )
        $ok = $false
        foreach ($u in $urls) {
            try {
                Invoke-WebRequest -Uri $u -OutFile $nsisZip -TimeoutSec 180 -ErrorAction Stop
                if ((Get-Item $nsisZip).Length -gt 100KB) { $ok = $true; break }
            } catch { Write-Host "    下载失败: $u" }
        }
        if (-not $ok) { throw "NSIS 下载失败，请手动安装 NSIS 后重试（https://nsis.sourceforge.io/Download）。" }
        Expand-Archive -LiteralPath $nsisZip -DestinationPath $nsisDir -Force
        # NSIS 压缩包顶层文件夹为 nsis-3.11/
        $extracted = Join-Path $nsisDir "nsis-$nsisVer"
        if (Test-Path (Join-Path $extracted "makensis.exe")) {
            Get-ChildItem $extracted | ForEach-Object { Move-Item $_.FullName -Destination $nsisDir -Force }
            Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
if (-not (Test-Path $nsisExe)) { throw "未找到 makensis.exe" }
$outExe = Join-Path $root "dist\DeepSeekHarness-Setup-v$Version-win-x64.exe"
& $nsisExe "/DVERSION=$Version" "/DAPP_SOURCE=$dist" "/DOUT=$outExe" "$root\installer.nsi"
if ($LASTEXITCODE -ne 0) { throw "NSIS 编译失败" }
Write-Host "    已生成: $outExe"

Write-Host ""
Write-Host "构建完成: $dist"
