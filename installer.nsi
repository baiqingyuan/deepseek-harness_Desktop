; installer.nsi - DeepSeek Harness Desktop 安装包
; 由 build.ps1 调用 makensis 编译，需传入:
;   /DVERSION=<版本号, 如 0.2.0>
;   /DAPP_SOURCE=<dist\DeepSeekHarness 的绝对路径>
;   /DOUT=<输出 exe 的绝对路径>
; 安装到 $LOCALAPPDATA\DeepSeekHarness（按用户安装，无需管理员权限 / 无 UAC 弹窗）。

!include "MUI2.nsh"
!include "FileFunc.nsh"

!ifndef VERSION
  !define VERSION "0.2.0"
!endif
!ifndef APP_SOURCE
  !define APP_SOURCE "dist\DeepSeekHarness"
!endif
!ifndef OUT
  !define OUT "dist\DeepSeekHarness-Setup.exe"
!endif

!define APP_NAME "DeepSeek Harness"
!define APP_EXE "DeepSeekHarness.exe"
!define APP_ICON "${APP_SOURCE}\DeepSeekHarness.ico"

Name "${APP_NAME}"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\DeepSeekHarness"
; 覆盖安装时沿用上次的安装目录：客户端更新传 /D= 之外，手动静默运行也能装回原处
InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "InstallLocation"
RequestExecutionLevel user
Unicode True
SetCompressor /SOLID lzma

!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "DeepSeek Harness 桌面版 安装向导"
!define MUI_DIRECTORYPAGE_TEXT_TOP "安装程序将把 ${APP_NAME} 安装到下面的文件夹。$\r$\n推荐保持默认（无需管理员权限）。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

; 覆盖安装（升级）前先结束正在运行的应用 —— 包括最小化到托盘常驻的实例，
; 否则 DeepSeekHarness.exe / node.exe 被占用会导致覆盖失败。
Function .onInit
  ; /T 必须保留：桌面壳直接拉起 node.exe 作为本地服务。只结束主进程会留下
  ; node 子进程继续锁定 $INSTDIR\node.exe，覆盖安装就会报「无法打开要写入的文件」。
  nsExec::Exec 'taskkill /IM "${APP_EXE}" /T /F'
  Pop $0
  Sleep 800
FunctionEnd

Section "Main" SecMain
  SetOutPath "$INSTDIR"
  ; 此时安装向导已确定最终 $INSTDIR。异常崩溃可能让 node 脱离原进程树，
  ; 因此在复制文件前再按完整路径清理一次；不会影响 Codex 等其它 Node 进程。
  ; NSIS 是 32 位进程，直接调用 powershell.exe 会被重定向到 32 位版本，后者读取
  ; 64 位 node.exe 的 Path 为空。Sysnative 明确进入 64 位 PowerShell 后才能精确匹配。
  nsExec::Exec '$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -Command "Get-Process node -ErrorAction SilentlyContinue | Where-Object Path -EQ $\"$INSTDIR\node.exe$\" | Stop-Process -Force -ErrorAction SilentlyContinue"'
  Pop $0
  Sleep 400
  ; 递归打包整个应用目录（node.exe / node_modules / DLL / exe 等）
  File /r "${APP_SOURCE}\*"

  ; 桌面 + 开始菜单快捷方式
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\DeepSeekHarness.ico"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\DeepSeekHarness.ico"

  ; 覆盖安装会换上新的应用图标：主动通知 Shell + 重建图标缓存，
  ; 否则桌面/开始菜单/任务栏很可能还继续显示旧图标（缓存按路径命中，文件换了也不重读）。
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)' ; SHCNE_ASSOCCHANGED
  nsExec::Exec '"$SYSDIR\ie4uinit.exe" -show'
  Pop $0

  ; 卸载程序
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 注册到「设置 → 应用 / 控制面板 → 卸载或更改程序」
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayIcon" "$INSTDIR\DeepSeekHarness.ico"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "Publisher" "DeepSeek Harness Desktop"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoRepair" 1
SectionEnd

; 静默安装（应用内更新用 /S 调用）时不显示任何界面，装完直接把新版拉起来 ——
; 这一步是「关闭应用 → 自主更新 → 自动重开」闭环的最后一段。
Function .onInstSuccess
  IfSilent 0 notsilent
  SetOutPath "$INSTDIR"
  Exec "$INSTDIR\${APP_EXE}"
notsilent:
FunctionEnd

Section "Uninstall"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
SectionEnd
