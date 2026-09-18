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
  nsExec::Exec 'taskkill /IM "${APP_EXE}" /F'
  Pop $0
  Sleep 800
FunctionEnd

Section "Main" SecMain
  SetOutPath "$INSTDIR"
  ; 递归打包整个应用目录（node.exe / node_modules / DLL / exe 等）
  File /r "${APP_SOURCE}\*"

  ; 桌面 + 开始菜单快捷方式
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\DeepSeekHarness.ico"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\DeepSeekHarness.ico"

  ; 卸载程序
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 注册到「设置 → 应用 / 控制面板 → 卸载或更改程序」
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayIcon" "$INSTDIR\DeepSeekHarness.ico"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "Publisher" "DeepSeek Harness Desktop"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayVersion" "${VERSION}"
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
