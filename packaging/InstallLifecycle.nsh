Function StopInstalledWidget
  IfFileExists "$INSTDIR\app\${PRODUCT_EXE}" 0 legacy
    ClearErrors
    ExecWait '"$StagePath\quotas.exe" --request-widget-exit --target-directory "$INSTDIR\app"' $0
    IfErrors stop_failed
    ${If} $0 != 0
      Goto stop_failed
    ${EndIf}
  legacy:
    IfFileExists "$INSTDIR\${PRODUCT_EXE}" 0 done
    ClearErrors
    ExecWait '"$StagePath\quotas.exe" --request-widget-exit --target-directory "$INSTDIR"' $0
    IfErrors stop_failed
    ${If} $0 != 0
      Goto stop_failed
    ${EndIf}
    Goto done
  stop_failed:
    SetErrorLevel 74
    MessageBox MB_OK|MB_ICONSTOP "Quota Widget is still running. Close it, then retry." /SD IDOK
    Abort
  done:
FunctionEnd

Function PreflightCurrentPayload
  IfFileExists "$INSTDIR\app\*.*" 0 done
  !include "${PAYLOAD_PREFLIGHT_INCLUDE}"
  Goto done
  preflight_failed:
    FileClose $9
    SetErrorLevel 75
    MessageBox MB_OK|MB_ICONSTOP "The installed files are locked. Close Quota Widget and retry." /SD IDOK
    Abort
  done:
FunctionEnd

Function CommitPayload
  StrCpy $BackupPath ""
  IfFileExists "$INSTDIR\app\*.*" 0 install_stage
    GetTempFileName $BackupPath "$INSTDIR"
    Delete "$BackupPath"
    ClearErrors
    Rename "$INSTDIR\app" "$BackupPath"
    ${If} ${Errors}
      SetErrorLevel 75
      MessageBox MB_OK|MB_ICONSTOP "The installed files are locked. Close Quota Widget and retry." /SD IDOK
      Abort
    ${EndIf}
  install_stage:
!ifdef TEST_FORCE_COMMIT_FAILURE
    FileOpen $9 "$INSTDIR\app" w
    FileClose $9
!endif
    ClearErrors
    Rename "$StagePath" "$INSTDIR\app"
    ${If} ${Errors}
!ifdef TEST_FORCE_COMMIT_FAILURE
      Delete "$INSTDIR\app"
!endif
      ${If} $BackupPath != ""
        ClearErrors
        Rename "$BackupPath" "$INSTDIR\app"
        ${If} ${Errors}
          SetErrorLevel 80
          MessageBox MB_OK|MB_ICONSTOP "The update failed. Previous files remain preserved at $BackupPath." /SD IDOK
          Abort
        ${EndIf}
        SetErrorLevel 76
        MessageBox MB_OK|MB_ICONSTOP "The update failed. The previous version was restored." /SD IDOK
        Abort
      ${EndIf}
      SetErrorLevel 76
      MessageBox MB_OK|MB_ICONSTOP "The new installation could not be committed. No previous version was changed." /SD IDOK
      Abort
    ${EndIf}
FunctionEnd
