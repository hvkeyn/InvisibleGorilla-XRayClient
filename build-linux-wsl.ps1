#Requires -Version 5.1

<#
.SYNOPSIS
    Автоматическая сборка Linux-релиза Invisible Gorilla XRay внутри WSL.

.DESCRIPTION
    Скрипт делает то же, что и ./build.sh, но запускает его из Windows внутри
    выбранного WSL-дистрибутива (по умолчанию — первый доступный).

    Алгоритм:
      1. Конвертирует путь репозитория в формат WSL (/mnt/x/...).
      2. Запускает build.sh с заданными параметрами (runtime, шаг, skip-deps).
      3. Копирует получившийся tar.gz из dist-linux в текущую папку Windows.

    Требования:
      * Установленный WSL (wsl --install).
      * Дистрибутив Ubuntu/Debian/ALT/Fedora с доступом в интернет.
      * Внутри WSL — права sudo для apt/dnf/zypper (build.sh сам ставит зависимости).

.PARAMETER Distro
    Имя WSL-дистрибутива (как в `wsl -l -v`). По умолчанию — используется default.

.PARAMETER Runtime
    Целевой runtime: linux-x64 (по умолчанию) или linux-arm64.

.PARAMETER Step
    Шаг сборки: all | go | tun2socks | geo | dotnet | bundle.

.PARAMETER SkipDeps
    Не устанавливать системные пакеты (если ВМ уже подготовлена).

.PARAMETER Configuration
    Debug | Release. По умолчанию Release.

.EXAMPLE
    .\build-linux-wsl.ps1
        Полная сборка linux-x64 в дефолтном WSL.

.EXAMPLE
    .\build-linux-wsl.ps1 -Distro Ubuntu -Runtime linux-arm64
        Сборка arm64 в дистрибутиве Ubuntu.

.EXAMPLE
    .\build-linux-wsl.ps1 -Step dotnet -SkipDeps
        Пересборка только .NET-части без установки пакетов.
#>

[CmdletBinding()]
param(
    [string]$Distro = "",
    [ValidateSet("linux-x64", "linux-arm64")]
    [string]$Runtime = "linux-x64",
    [ValidateSet("all", "deps", "go", "tun2socks", "geo", "dotnet", "bundle")]
    [string]$Step = "all",
    [switch]$SkipDeps,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Write-Header($text) {
    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host ""
}

function Test-WslAvailable {
    try {
        $null = & wsl.exe --status 2>$null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Convert-PathToWsl($winPath) {
    $resolved = (Resolve-Path -LiteralPath $winPath).Path
    $drive = $resolved.Substring(0, 1).ToLower()
    $rest = $resolved.Substring(2) -replace "\\", "/"
    return "/mnt/$drive$rest"
}

Write-Header "Invisible Gorilla XRay :: Linux build via WSL"

if (-not (Test-WslAvailable)) {
    Write-Error "WSL не обнаружен. Установите его командой 'wsl --install' и перезагрузите ПК."
    exit 1
}

$repoRoot = $PSScriptRoot
$wslRepo = Convert-PathToWsl $repoRoot
Write-Host "Repo (Windows): $repoRoot"
Write-Host "Repo (WSL):     $wslRepo"
Write-Host "Distro:         $($(if ($Distro) { $Distro } else { '(default)' }))"
Write-Host "Runtime:        $Runtime"
Write-Host "Step:           $Step"
Write-Host "Configuration:  $Configuration"
Write-Host "Skip deps:      $SkipDeps"

$args = @("--cd", $wslRepo)
if ($Distro) { $args = @("-d", $Distro) + $args }
$args += @("--", "bash", "-lc")

$skipFlag = ""
if ($SkipDeps) { $skipFlag = "--skip-deps" }

$cmd = "chmod +x ./build.sh && ./build.sh --step $Step --runtime $Runtime --config $Configuration $skipFlag"
$args += $cmd

Write-Header "Running build.sh inside WSL"
Write-Host "wsl.exe $($args -join ' ')" -ForegroundColor DarkGray

& wsl.exe @args
if ($LASTEXITCODE -ne 0) {
    Write-Error "Сборка в WSL завершилась с кодом $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Header "Collecting artifacts"
$distDir = Join-Path $repoRoot "dist-linux"
if (Test-Path $distDir) {
    $archives = Get-ChildItem -LiteralPath $distDir -Filter "InvisibleGorilla-XRay-Linux-*.tar.gz" -ErrorAction SilentlyContinue
    if ($archives) {
        foreach ($a in $archives) {
            $size = "{0:N1} MB" -f ($a.Length / 1MB)
            Write-Host "  $($a.FullName)  ($size)" -ForegroundColor Green
        }
    } else {
        Write-Host "  (no archives found in $distDir)" -ForegroundColor Yellow
    }
} else {
    Write-Host "  $distDir missing - bundle step was not run." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
