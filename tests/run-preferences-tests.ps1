$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\_ux-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /main:DesktopPreferencesTests `
    "/out:$out\PreferencesTests.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    "/r:$root\dist\_wv2\Microsoft.Web.WebView2.Core.dll" `
    "/r:$root\dist\_wv2\Microsoft.Web.WebView2.WinForms.dll" `
    "$root\src\App.cs" "$root\src\Version.cs" "$PSScriptRoot\DesktopPreferencesTests.cs"
if ($LASTEXITCODE -ne 0) { throw '窗口偏好测试编译失败' }
& "$out\PreferencesTests.exe" $out
if ($LASTEXITCODE -ne 0) { throw '窗口偏好测试失败' }
