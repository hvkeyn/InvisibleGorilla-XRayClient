<#
.SYNOPSIS
    Removes low-integrity labels from the executables of a published build.

.DESCRIPTION
    Windows starts a process with the integrity level of its image file label. Files copied
    out of a sandboxed workspace can carry a "Low" (or "Medium") mandatory label, and then
    the app runs at that level: it cannot write %LOCALAPPDATA%, cannot open HKCU for writing
    (system proxy), cannot add a notification icon, and cannot create a Wintun adapter (TUN).
    The same build works fine once the labels are raised.

    Only *.exe files matter, because the image label drives the process token; touching every
    file of a big publish folder takes minutes for no benefit.

.PARAMETER Path
    Folder to process (searched recursively). Defaults to the Windows x64 publish folder.

.PARAMETER All
    Also relabel every file (slow). Not needed for the integrity problem.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\fix-publish-integrity.ps1
#>
[CmdletBinding()]
param(
    [string] $Path = (Join-Path (Split-Path $PSScriptRoot -Parent) 'InvisibleGorilla-XRay-Windows-win-x64'),
    [switch] $All
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "Folder not found: $Path"
}

$resolved = (Resolve-Path -LiteralPath $Path).Path
Write-Host "Raising integrity labels under $resolved"

$targets = if ($All) {
    @($resolved)
} else {
    @(Get-ChildItem -LiteralPath $resolved -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName })
}

if ($targets.Count -eq 0) {
    Write-Host "Nothing to do."
    return
}

$failed = 0
foreach ($target in $targets) {
    & icacls $target /setintegritylevel '(OI)(CI)High' /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $failed++
    }
}

if ($failed -gt 0) {
    Write-Error "$failed of $($targets.Count) entries could not be relabeled"
}

Write-Host "Done: $($targets.Count) executable(s) now start at the caller's integrity level."
