Unicode true
RequestExecutionLevel user
!ifdef TEST_BUILD
SetCompressor zlib
!else
SetCompressor /SOLID lzma
SetCompressorDictSize 32
!endif

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef PAYLOAD_DIR
  !error "PAYLOAD_DIR is required"
!endif
!ifndef PAYLOAD_DELETE_INCLUDE
  !error "PAYLOAD_DELETE_INCLUDE is required"
!endif
!ifndef PAYLOAD_PREFLIGHT_INCLUDE
  !error "PAYLOAD_PREFLIGHT_INCLUDE is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif

!define PRODUCT_NAME "Quota Widget"
!define PRODUCT_PUBLISHER "Quota Widget contributors"
!define PRODUCT_URL "https://github.com/CustomDigitalServices-Kevin/claude-codex-quota-widget"
!define PRODUCT_EXE "QuotaWidget.exe"

!ifdef TEST_BUILD
  !ifndef TEST_SUFFIX
    !error "TEST_SUFFIX is required for a test build"
  !endif
  !define APP_REG_KEY "Software\QuotaWidget\InstallerTests\${TEST_SUFFIX}"
  !define UNINSTALL_KEY "${APP_REG_KEY}\Uninstall"
  !define RUN_KEY "${APP_REG_KEY}\Run"
  !define RUN_VALUE "QuotaWidget"
  !define MENU_NAME "Quota Widget Test ${TEST_SUFFIX}"
  !define DESKTOP_NAME "Quota Widget Test ${TEST_SUFFIX}.lnk"
  !define INSTALL_ROOT "$LOCALAPPDATA\QuotaWidget-InstallerTests\${TEST_SUFFIX}"
  !define DATA_DIR "$INSTDIR-data"
  !define CLAUDE_SETTINGS "$INSTDIR-data\synthetic-claude\settings.json"
!else
  !define APP_REG_KEY "Software\QuotaWidget"
  !define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\QuotaWidget"
  !define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
  !define RUN_VALUE "QuotaWidget"
  !define MENU_NAME "Quota Widget"
  !define DESKTOP_NAME "Quota Widget.lnk"
  !define INSTALL_ROOT "$LOCALAPPDATA\Programs\QuotaWidget"
  !define DATA_DIR "$LOCALAPPDATA\QuotaWidget"
!endif

Name "${PRODUCT_NAME} ${VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "${INSTALL_ROOT}"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
BrandingText "Quota Widget ${VERSION}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "Quota Widget Windows installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "MIT License"

Var StartWithWindows
Var DesktopShortcut
Var ConfigureClaude
Var StartCheckbox
Var DesktopCheckbox
Var ClaudeCheckbox
Var StagePath
Var BackupPath

!include "ShortcutOwnership.nsh"
!include "InstallLifecycle.nsh"

!define MUI_ABORTWARNING
!ifndef TEST_BUILD
  !define MUI_FINISHPAGE_RUN "$INSTDIR\app\${PRODUCT_EXE}"
  !define MUI_FINISHPAGE_RUN_TEXT "Launch Quota Widget"
!endif
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom OptionsPage OptionsPageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  SetShellVarContext current
  ${IfNot} ${AtLeastWin11}
    MessageBox MB_OK|MB_ICONSTOP "Quota Widget requires Windows 11 (build 22000 or newer)." /SD IDOK
    SetErrorLevel 78
    Abort
  ${EndIf}
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_OK|MB_ICONSTOP "This package requires Windows 11 x64." /SD IDOK
    SetErrorLevel 79
    Abort
  ${EndIf}
  StrCpy $StartWithWindows ${BST_CHECKED}
  StrCpy $DesktopShortcut ${BST_UNCHECKED}
  StrCpy $ConfigureClaude ${BST_UNCHECKED}
!ifdef CLAUDE_TEST_OPTIN
  StrCpy $ConfigureClaude ${BST_CHECKED}
!endif
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ReadRegStr $1 HKCU "${RUN_KEY}" "${RUN_VALUE}"
  ${If} $1 == ""
    ${If} $0 != ""
      StrCpy $StartWithWindows ${BST_UNCHECKED}
    ${Else}
      IfFileExists "$INSTDIR\QuotaWidget.exe" 0 +2
        StrCpy $StartWithWindows ${BST_UNCHECKED}
    ${EndIf}
  ${EndIf}
  IfFileExists "$DESKTOP\${DESKTOP_NAME}" 0 +2
    StrCpy $DesktopShortcut ${BST_CHECKED}
FunctionEnd

Function OptionsPage
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 24u "Choose optional integrations. Claude access is disabled by default."
  Pop $0
  ${NSD_CreateCheckbox} 0 34u 100% 12u "Start Quota Widget with Windows"
  Pop $StartCheckbox
  ${NSD_SetState} $StartCheckbox $StartWithWindows
  ${NSD_CreateCheckbox} 0 56u 100% 12u "Create a desktop shortcut"
  Pop $DesktopCheckbox
  ${NSD_SetState} $DesktopCheckbox $DesktopShortcut
  ${NSD_CreateCheckbox} 0 78u 100% 24u "Connect Claude status line (changes Claude settings with a backup)"
  Pop $ClaudeCheckbox
  ${NSD_SetState} $ClaudeCheckbox $ConfigureClaude
  nsDialogs::Show
FunctionEnd

Function OptionsPageLeave
  ${NSD_GetState} $StartCheckbox $StartWithWindows
  ${NSD_GetState} $DesktopCheckbox $DesktopShortcut
  ${NSD_GetState} $ClaudeCheckbox $ConfigureClaude
FunctionEnd

Function WriteRegistration
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "${PRODUCT_URL}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${If} $StartWithWindows == ${BST_CHECKED}
    WriteRegStr HKCU "${RUN_KEY}" "${RUN_VALUE}" '$\"$INSTDIR\app\${PRODUCT_EXE}$\"'
  ${Else}
    DeleteRegValue HKCU "${RUN_KEY}" "${RUN_VALUE}"
  ${EndIf}
FunctionEnd

Function ConfigureClaudeIfSelected
  ${If} $ConfigureClaude != ${BST_CHECKED}
    Return
  ${EndIf}
  CreateDirectory "${DATA_DIR}"
!ifdef TEST_BUILD
  CreateDirectory "$INSTDIR-data\synthetic-claude"
  ExecWait '"$INSTDIR\app\quotas.exe" --configure-claude --apply --settings-path "${CLAUDE_SETTINGS}" --integration-path "${DATA_DIR}\claude-integration.json"' $0
!else
  ExecWait '"$INSTDIR\app\quotas.exe" --configure-claude --apply --integration-path "${DATA_DIR}\claude-integration.json"' $0
!endif
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "Quota Widget is installed, but Claude was not changed. Configure it later if needed." /SD IDOK
  ${EndIf}
FunctionEnd

Section "Install" SEC_MAIN
  CreateDirectory "$INSTDIR"
  GetTempFileName $StagePath "$INSTDIR"
  Delete "$StagePath"
  CreateDirectory "$StagePath"
  SetOutPath "$StagePath"
  ClearErrors
  File /r "${PAYLOAD_DIR}\*.*"
  IfErrors extract_failed
  IfFileExists "$StagePath\${PRODUCT_EXE}" +2 0
    Goto extract_failed
  IfFileExists "$StagePath\quotas.exe" +2 0
    Goto extract_failed
  SetOutPath "$INSTDIR"
  Call StopInstalledWidget
  Call PreflightCurrentPayload
  Call CommitPayload
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  Call WriteRegistration
  Call WriteOwnedShortcuts
  Call ConfigureClaudeIfSelected
  !include "LegacyV1Files.nsh"
  ${If} $BackupPath != ""
    !define PAYLOAD_ROOT "$BackupPath"
    !include "${PAYLOAD_DELETE_INCLUDE}"
    !undef PAYLOAD_ROOT
    RMDir "$BackupPath"
  ${EndIf}
  Goto install_done
  extract_failed:
    SetErrorLevel 77
    MessageBox MB_OK|MB_ICONSTOP "The installation payload could not be extracted safely." /SD IDOK
    Abort
  install_done:
SectionEnd

Function .onInstFailed
  ${If} $StagePath != ""
    RMDir /r "$StagePath"
  ${EndIf}
FunctionEnd

Function un.onInit
  SetShellVarContext current
FunctionEnd

Function un.StopWidget
  IfFileExists "$INSTDIR\app\quotas.exe" 0 done
  ExecWait '"$INSTDIR\app\quotas.exe" --request-widget-exit --target-directory "$INSTDIR\app"' $0
  ${If} $0 != 0
    SetErrorLevel 74
    MessageBox MB_OK|MB_ICONSTOP "Quota Widget is still running. Close it, then retry." /SD IDOK
    Abort
  ${EndIf}
  done:
FunctionEnd

Function un.RemoveOwnedClaudeIntegration
  IfFileExists "${DATA_DIR}\claude-integration.json" 0 done
!ifdef TEST_BUILD
  ExecWait '"$INSTDIR\app\quotas.exe" --unconfigure-claude --settings-path "${CLAUDE_SETTINGS}" --integration-path "${DATA_DIR}\claude-integration.json"' $0
!else
  ExecWait '"$INSTDIR\app\quotas.exe" --unconfigure-claude --integration-path "${DATA_DIR}\claude-integration.json"' $0
!endif
  ${If} $0 != 0
    DetailPrint "Claude settings were preserved because ownership could not be confirmed."
  ${EndIf}
  done:
FunctionEnd

Function un.RemoveOwnedRunValue
  ReadRegStr $0 HKCU "${RUN_KEY}" "${RUN_VALUE}"
  StrCpy $1 '$\"$INSTDIR\app\${PRODUCT_EXE}$\"'
  ${If} $0 == $1
    DeleteRegValue HKCU "${RUN_KEY}" "${RUN_VALUE}"
  ${EndIf}
FunctionEnd

Section "Uninstall"
  Call un.StopWidget
  Call un.RemoveOwnedClaudeIntegration
  Call un.RemoveOwnedRunValue
  Call un.RemoveOwnedShortcuts
  !define PAYLOAD_ROOT "$INSTDIR\app"
  !include "${PAYLOAD_DELETE_INCLUDE}"
  !undef PAYLOAD_ROOT
  RMDir "$INSTDIR\app"
  Delete "$INSTDIR\Uninstall.exe"
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ${EndIf}
!ifdef TEST_BUILD
  DeleteRegKey HKCU "${APP_REG_KEY}"
!endif
  RMDir "$INSTDIR"
SectionEnd
