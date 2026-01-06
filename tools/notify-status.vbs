Option Explicit

Dim ps1Path
If WScript.Arguments.Count < 1 Then
    WScript.Quit 2
End If

ps1Path = WScript.Arguments.Item(0)

Dim shell, cmd
Set shell = CreateObject("WScript.Shell")
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & ps1Path & """ -NotifyStatus"

' Run hidden, do not wait.
shell.Run cmd, 0, False

