; ============================================================
;  测速浮光 · 安装包
; ============================================================
;  产物：release\测速浮光-v0.1.0-win64-setup.exe
;
;  设计取舍：
;  1. 装到 %LOCALAPPDATA%\测速浮光，RequestExecutionLevel user ——
;     不弹 UAC。一个测速球要管理员权限，用户第一反应是"这软件想干嘛"。
;  2. 桌面快捷方式指向「网络测速球.exe」，不是浮光面板。
;     用户看到的入口是球，浮光是球右键菜单里的东西 —— 和日常用法一致。
;  3. 卸载**不删** %APPDATA%\浮光\（笔记、图片、设置）。
;     那是用户自己写的内容，不是安装产物；升级/重装也不该丢。
;  4. 两个 exe 都装进同一个目录：球是按「同目录有没有 浮光面板.exe」找人的。
; ============================================================

Unicode true

!include "MUI2.nsh"

!define APP_NAME "测速浮光"
!define APP_VERSION "0.1.0"
!define APP_EXE "网络测速球.exe"
!define PANEL_EXE "浮光面板.exe"
!define PUBLISHER "zhangmoddd"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "..\release\测速浮光-v${APP_VERSION}-win64-setup.exe"
InstallDir "$LOCALAPPDATA\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32

VIProductVersion "0.1.0.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" ""

!define MUI_ICON "..\ball\assets\NetSpeedBall.ico"
!define MUI_UNICON "..\ball\assets\NetSpeedBall.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立刻运行 ${APP_NAME}"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\使用说明.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "看一眼使用说明"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

Section "安装" SecMain
  SetOutPath "$INSTDIR"
  File "..\release\测速浮光\${APP_EXE}"
  File "..\release\测速浮光\${PANEL_EXE}"
  File "..\release\测速浮光\使用说明.txt"

  WriteUninstaller "$INSTDIR\卸载.exe"

  ; 桌面和开始菜单都指向**球**：它是用户真正双击的那个入口
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\卸载.exe"

  ; 「设置 → 应用」里能看到的卸载项
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\卸载.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  WriteRegStr HKCU "Software\${APP_NAME}" "InstallDir" "$INSTDIR"

  ; 开机自启由球自己管（设置里的开关），安装程序不写启动项 ——
  ; 两处都写会出现"关了开关但开机还是起来"
SectionEnd

Section "Uninstall"
  ; 只删安装产物。%APPDATA%\浮光\ 里的笔记和图片是用户的东西，留着
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\${PANEL_EXE}"
  Delete "$INSTDIR\使用说明.txt"
  Delete "$INSTDIR\卸载.exe"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APP_NAME}"

  MessageBox MB_ICONINFORMATION|MB_OK "已卸载。$\r$\n$\r$\n你的笔记和设置还留在 %APPDATA%\浮光\，没有删。$\r$\n不需要的话可以手动删掉那个文件夹。"
SectionEnd
