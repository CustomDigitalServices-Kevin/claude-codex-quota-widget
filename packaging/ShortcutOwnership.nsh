!macro ReadShortcutOwnership Path Prefix Result
  StrCpy ${Result} 0
  ${If} ${FileExists} "${Path}"
    ClearErrors
    ReadRegDWORD $7 HKCU "${UNINSTALL_KEY}" "${Prefix}TimeHigh"
    ReadRegDWORD $8 HKCU "${UNINSTALL_KEY}" "${Prefix}TimeLow"
    ${IfNot} ${Errors}
      GetFileTime "${Path}" $5 $6
      ${If} $5 == $7
      ${AndIf} $6 == $8
        StrCpy ${Result} 1
      ${EndIf}
    ${EndIf}
  ${EndIf}
!macroend

!macro StoreOwnedShortcut Path Target Prefix
  !insertmacro ReadShortcutOwnership "${Path}" "${Prefix}" $4
  ${IfNot} ${FileExists} "${Path}"
  ${OrIf} $4 == 1
    CreateShortcut "${Path}" "${Target}"
    GetFileTime "${Path}" $5 $6
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "${Prefix}TimeHigh" $5
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "${Prefix}TimeLow" $6
  ${Else}
    DetailPrint "Preserved an unowned shortcut: ${Path}"
  ${EndIf}
!macroend

!macro RemoveOwnedShortcut Path Prefix
  !insertmacro ReadShortcutOwnership "${Path}" "${Prefix}" $4
  ${If} $4 == 1
    Delete "${Path}"
  ${ElseIf} ${FileExists} "${Path}"
    DetailPrint "Preserved a changed or unowned shortcut: ${Path}"
  ${EndIf}
!macroend

Function WriteOwnedShortcuts
  CreateDirectory "$SMPROGRAMS\${MENU_NAME}"
  !insertmacro StoreOwnedShortcut "$SMPROGRAMS\${MENU_NAME}\Quota Widget.lnk" "$INSTDIR\app\${PRODUCT_EXE}" "MenuApp"
  !insertmacro StoreOwnedShortcut "$SMPROGRAMS\${MENU_NAME}\Uninstall Quota Widget.lnk" "$INSTDIR\Uninstall.exe" "MenuUninstall"
  ${If} $DesktopShortcut == ${BST_CHECKED}
    !insertmacro StoreOwnedShortcut "$DESKTOP\${DESKTOP_NAME}" "$INSTDIR\app\${PRODUCT_EXE}" "Desktop"
  ${Else}
    !insertmacro RemoveOwnedShortcut "$DESKTOP\${DESKTOP_NAME}" "Desktop"
  ${EndIf}
FunctionEnd

Function un.RemoveOwnedShortcuts
  !insertmacro RemoveOwnedShortcut "$DESKTOP\${DESKTOP_NAME}" "Desktop"
  !insertmacro RemoveOwnedShortcut "$SMPROGRAMS\${MENU_NAME}\Quota Widget.lnk" "MenuApp"
  !insertmacro RemoveOwnedShortcut "$SMPROGRAMS\${MENU_NAME}\Uninstall Quota Widget.lnk" "MenuUninstall"
  RMDir "$SMPROGRAMS\${MENU_NAME}"
FunctionEnd
