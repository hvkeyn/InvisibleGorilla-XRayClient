' Launcher for the desktop shortcut.
'
' A plain "schtasks /run" in the shortcut showed a console window and failed
' with ERROR_TASK_ALREADY_RUNNING (0x800710E0) while the task instance lived,
' i.e. while the app was running - so clicking the shortcut after the window
' had been hidden in the tray did nothing at all.
'
' Now: if the app is running, start a second instance, which signals the open
' one over its control pipe to show the window; otherwise run the scheduled
' task, which starts the app elevated without a UAC prompt, and fall back to a
' plain start when the task is missing.

Option Explicit

Dim fso, shell, scriptDir, repoDir, exePath, taskName
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
repoDir = fso.GetParentFolderName(scriptDir)
exePath = fso.BuildPath(repoDir, "InvisibleGorilla-XRay-Windows-win-x64\Invisible Gorilla XRay.exe")
taskName = "InvisibleGorillaXRay"

If Not fso.FileExists(exePath) Then
    MsgBox "Не найден файл приложения:" & vbCrLf & exePath, 16, "Invisible Gorilla XRay"
    WScript.Quit 1
End If

Dim wmi, query, item, isRunning
Set wmi = GetObject("winmgmts:\\.\root\cimv2")

isRunning = False
Set query = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name = 'Invisible Gorilla XRay.exe'")
For Each item In query
    isRunning = True
Next

If isRunning Then
    ' Second instance signals the open one and exits; 1 = normal window.
    shell.Run Chr(34) & exePath & Chr(34), 1, False
Else
    ' 0 = hidden console; the task starts the app elevated without a prompt.
    Dim rc
    rc = shell.Run("schtasks.exe /run /tn " & Chr(34) & taskName & Chr(34), 0, True)
    If rc <> 0 Then
        shell.Run Chr(34) & exePath & Chr(34), 1, False
    End If
End If
