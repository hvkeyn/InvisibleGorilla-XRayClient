<#
.SYNOPSIS
    Removes low-integrity labels from a published build.

.DESCRIPTION
    Windows starts an executable with the integrity level of its file label. Files copied
    from a sandboxed workspace can carry a "Low" (or "Medium") mandatory label, and then
    the app runs at that level: it cannot write %LOCALAPPDATA%, cannot open HKCU for
    writing (system proxy), cannot add a notification icon, and cannot create a Wintun
    adapter (TUN). The same build works fine when published without those labels.

    This script raises every file of the given folder to "High", which is what an
    elevated process would have anyway. On a healthy machine the files have no label,
    and setting one changes nothing for the running app.

.PARAMETER Path
    Folder to process. Defaults to the Windows x64 publish folder of this repository.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\fix-publish-integrity.ps1
#>
[CmdletBinding()]
param(
    [string] $Path = (Join-Path (Split-Path $PSScriptRoot -Parent) 'InvisibleGorilla-XRay-Windows-win-x64')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "Folder not found: $Path"
}

$resolved = (Resolve-Path -LiteralPath $Path).Path
Write-Host "Raising integrity labels under $resolved"

# /T applies to the folder and every file, /C continues past errors, /Q hides the per-file log.
& icacls $resolved /setintegritylevel '(OI)(CI)High' /T /C /Q | Out-Null
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Error "icacls failed with exit code $exitCode"
}

Write-Host "Done. Executables in this folder now start at the caller's integrity level."
