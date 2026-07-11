Option Explicit

Dim shell, command, index, value, exitCode

If WScript.Arguments.Count = 0 Then
    WScript.Quit 64
End If

command = ""
For index = 0 To WScript.Arguments.Count - 1
    value = Replace(WScript.Arguments(index), Chr(34), "\" & Chr(34))
    If Len(command) > 0 Then command = command & " "
    command = command & Chr(34) & value & Chr(34)
Next

Set shell = CreateObject("WScript.Shell")
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode
