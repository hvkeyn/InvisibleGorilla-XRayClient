#Requires -Version 5.1

<#
.SYNOPSIS
    Скрипт сборки Invisible Gorilla - XRay Client.

.DESCRIPTION
    Автоматизирует полный цикл сборки проекта:
    1. Проверка и автоустановка зависимостей (Go, .NET SDK) напрямую
    2. Сборка Go-обёртки XRayCore.dll
    3. Скачивание geoip.dat и geosite.dat
    4. Скачивание InvisibleMan-TUN (опционально)
    5. Сборка/публикация .NET приложения

.PARAMETER Step
    Выполнить конкретный шаг сборки:
    - All       : Все шаги (по умолчанию)
    - GoWrapper : Только сборка Go-обёртки
    - GeoFiles  : Только скачивание geo-файлов
    - TUN       : Только скачивание TUN-сервиса
    - DotNet    : Только сборка .NET приложения

.PARAMETER Configuration
    Конфигурация сборки .NET: Debug или Release. По умолчанию Release.

.PARAMETER Publish
    Если указан, выполняет dotnet publish вместо dotnet build.

.PARAMETER Runtime
    Целевой runtime для публикации. По умолчанию win-x64.

.PARAMETER OutputDir
    Директория для результата публикации. По умолчанию ./publish.

.PARAMETER SkipTUN
    Пропустить скачивание InvisibleMan-TUN.

.EXAMPLE
    .\build.ps1
    Полная сборка со всеми шагами (с автоустановкой зависимостей).

.EXAMPLE
    .\build.ps1 -Publish
    Полная сборка с публикацией в single-file.

.EXAMPLE
    .\build.ps1 -Step GoWrapper
    Только сборка Go-обёртки.

.EXAMPLE
    .\build.ps1 -Step DotNet -Configuration Debug
    Только сборка .NET в режиме Debug.
#>

[CmdletBinding()]
param(
    [ValidateSet("All", "GoWrapper", "GeoFiles", "TUN", "DotNet")]
    [string]$Step = "All",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Publish,

    [string]$Runtime = "win-x64",

    [string]$OutputDir = ".\publish",

    [switch]$SkipTUN
)

# ─── Настройки ────────────────────────────────────────────────────────────────

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RootDir      = $PSScriptRoot
$WrapperDir   = Join-Path $RootDir "XRay-Wrapper"
$AppDir       = Join-Path $RootDir "InvisibleGorilla-XRay"
$LibrariesDir = Join-Path $AppDir "Libraries"
$TunDir       = Join-Path $AppDir "TUN"
$SolutionFile = Join-Path $RootDir "InvisibleGorilla-XRay.sln"

$GeoIpUrl     = "https://github.com/v2fly/geoip/releases/latest/download/geoip.dat"
$GeoSiteUrl   = "https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat"
$TunRelease   = "https://api.github.com/repos/InvisibleManVPN/InvisibleMan-TUN/releases/latest"

# ─── Вспомогательные функции ──────────────────────────────────────────────────

function Write-StepHeader {
    param([string]$Message)
    $separator = "=" * 60
    Write-Host ""
    Write-Host $separator -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host $separator -ForegroundColor Cyan
    Write-Host ""
}

function Write-Success {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "[..] $Message" -ForegroundColor Yellow
}

function Write-Err {
    param([string]$Message)
    Write-Host "[!!] $Message" -ForegroundColor Red
}

function Test-Command {
    param([string]$Name)
    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Update-SessionPath {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath    = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path    = "$machinePath;$userPath"
}

# ─── Установка Go напрямую с go.dev ──────────────────────────────────────────

function Install-Go {
    Write-Info "Запрос последней версии Go..."

    try {
        $dlInfo = Invoke-RestMethod -Uri "https://go.dev/dl/?mode=json" -UseBasicParsing
        $latest = $dlInfo[0]
        $goVersion = $latest.version

        $file = $latest.files | Where-Object {
            $_.os -eq "windows" -and $_.arch -eq "amd64" -and $_.kind -eq "installer"
        } | Select-Object -First 1

        if (-not $file) {
            $file = $latest.files | Where-Object {
                $_.os -eq "windows" -and $_.arch -eq "amd64"
            } | Select-Object -First 1
        }

        $downloadUrl = "https://go.dev/dl/$($file.filename)"
    }
    catch {
        $goVersion = "go1.23.6"
        $downloadUrl = "https://go.dev/dl/go1.23.6.windows-amd64.msi"
        Write-Info "Не удалось запросить API, использую $goVersion"
    }

    $msiPath = Join-Path $env:TEMP "$goVersion.windows-amd64.msi"

    Write-Info "Скачивание $goVersion..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $msiPath -UseBasicParsing

    Write-Info "Установка $goVersion (msiexec, может потребоваться UAC)..."
    $proc = Start-Process -FilePath "msiexec.exe" `
        -ArgumentList "/i `"$msiPath`" /quiet /norestart" `
        -Wait -PassThru

    Remove-Item $msiPath -Force -ErrorAction SilentlyContinue

    if ($proc.ExitCode -ne 0) {
        Write-Err "msiexec завершился с кодом $($proc.ExitCode)"
        Write-Err "Попробуйте запустить скрипт от имени администратора"
        exit 1
    }

    Update-SessionPath

    if (-not (Test-Command "go")) {
        $goRoot = "C:\Program Files\Go\bin"
        if (Test-Path $goRoot) {
            $env:Path = "$goRoot;$env:Path"
        }
    }

    Write-Success "$goVersion установлен"
}

# ─── Установка .NET SDK через официальный скрипт Microsoft ───────────────────

function Install-DotNetSdk {
    param([string]$Channel = "7.0")

    $installScript = Join-Path $env:TEMP "dotnet-install.ps1"

    Write-Info "Скачивание dotnet-install.ps1..."
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript -UseBasicParsing

    Write-Info "Установка .NET SDK $Channel..."
    & $installScript -Channel $Channel -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"

    Remove-Item $installScript -Force -ErrorAction SilentlyContinue

    $dotnetDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
    if (Test-Path $dotnetDir) {
        if ($env:Path -notlike "*$dotnetDir*") {
            $env:Path = "$dotnetDir;$env:Path"
        }

        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        if ($userPath -notlike "*$dotnetDir*") {
            [Environment]::SetEnvironmentVariable("Path", "$dotnetDir;$userPath", "User")
            Write-Info "dotnet добавлен в PATH пользователя"
        }
    }

    Write-Success ".NET SDK $Channel установлен"
}

# ─── Проверка и установка зависимостей ────────────────────────────────────────

function Ensure-Go {
    if (Test-Command "go") {
        $goVer = & go version 2>&1 | Select-String -Pattern 'go\d+\.\d+(\.\d+)?' | ForEach-Object { $_.Matches[0].Value }
        Write-Success "Go $goVer"
        return
    }

    Write-Info "Go не найден, начинаю установку..."
    Install-Go

    if (-not (Test-Command "go")) {
        Write-Err "Go установлен, но не найден в PATH."
        Write-Err "Перезапустите терминал и запустите скрипт снова."
        exit 1
    }

    $goVer = & go version 2>&1 | Select-String -Pattern 'go\d+\.\d+(\.\d+)?' | ForEach-Object { $_.Matches[0].Value }
    Write-Success "Go $goVer (установлен)"
}

function Ensure-DotNet {
    if (Test-Command "dotnet") {
        $sdkList = & dotnet --list-sdks 2>&1
        $has7 = $sdkList | Select-String -Pattern '^7\.'
        if ($has7) {
            $ver = ($has7 | Select-Object -First 1).ToString().Split(' ')[0]
            Write-Success ".NET SDK $ver"
            return
        }
        Write-Info ".NET SDK найден, но версия 7.x отсутствует"
    }
    else {
        Write-Info ".NET SDK не найден, начинаю установку..."
    }

    Install-DotNetSdk -Channel "7.0"

    if (-not (Test-Command "dotnet")) {
        Write-Err ".NET SDK установлен, но не найден в PATH."
        Write-Err "Перезапустите терминал и запустите скрипт снова."
        exit 1
    }

    $ver = (& dotnet --version 2>&1).Trim()
    Write-Success ".NET SDK $ver (установлен)"
}

function Test-Prerequisites {
    param([string]$BuildStep)

    Write-StepHeader "Проверка и установка зависимостей"

    $needGo     = $BuildStep -in @("All", "GoWrapper")
    $needDotNet = $BuildStep -in @("All", "DotNet")

    if ($needGo) {
        Ensure-Go
    }

    if ($needDotNet) {
        Ensure-DotNet
    }

    Write-Success "Все зависимости готовы"
}

# ─── Шаг 1: Сборка Go-обёртки ────────────────────────────────────────────────

function Build-GoWrapper {
    Write-StepHeader "Шаг 1: Сборка XRayCore.dll (Go)"

    if (-not (Test-Path $WrapperDir)) {
        Write-Err "Директория XRay-Wrapper не найдена: $WrapperDir"
        exit 1
    }

    Push-Location $WrapperDir
    try {
        Write-Info "Сборка XRayCore.dll..."
        $env:CGO_ENABLED = "1"
        & go build --buildmode=c-shared -o XRayCore.dll -trimpath -ldflags "-s -w -buildid=" .
        if ($LASTEXITCODE -ne 0) {
            Write-Err "Сборка Go завершилась с ошибкой (код: $LASTEXITCODE)"
            exit $LASTEXITCODE
        }
        Write-Success "XRayCore.dll собран"

        if (-not (Test-Path $LibrariesDir)) {
            New-Item -ItemType Directory -Path $LibrariesDir -Force | Out-Null
            Write-Info "Создана директория: $LibrariesDir"
        }

        Copy-Item -Path "XRayCore.dll" -Destination $LibrariesDir -Force
        Write-Success "XRayCore.dll -> $LibrariesDir"

        if (Test-Path "XRayCore.h") {
            Remove-Item "XRayCore.h" -Force -ErrorAction SilentlyContinue
        }
        Remove-Item "XRayCore.dll" -Force -ErrorAction SilentlyContinue
    }
    finally {
        Pop-Location
    }
}

# ─── Шаг 2: Скачивание geo-файлов ────────────────────────────────────────────

function Get-GeoFiles {
    Write-StepHeader "Шаг 2: Скачивание geoip.dat и geosite.dat"

    $geoIpPath   = Join-Path $AppDir "geoip.dat"
    $geoSitePath = Join-Path $AppDir "geosite.dat"

    Write-Info "Скачивание geoip.dat..."
    try {
        Invoke-WebRequest -Uri $GeoIpUrl -OutFile $geoIpPath -UseBasicParsing
        $sizeMB = [math]::Round((Get-Item $geoIpPath).Length / 1MB, 1)
        Write-Success "geoip.dat ($sizeMB MB)"
    }
    catch {
        Write-Err "Не удалось скачать geoip.dat: $_"
        exit 1
    }

    Write-Info "Скачивание geosite.dat..."
    try {
        Invoke-WebRequest -Uri $GeoSiteUrl -OutFile $geoSitePath -UseBasicParsing
        $sizeMB = [math]::Round((Get-Item $geoSitePath).Length / 1MB, 1)
        Write-Success "geosite.dat ($sizeMB MB)"
    }
    catch {
        Write-Err "Не удалось скачать geosite.dat: $_"
        exit 1
    }
}

# ─── Шаг 3: Скачивание TUN-сервиса ──────────────────────────────────────────

function Get-TunService {
    Write-StepHeader "Шаг 3: Скачивание InvisibleMan-TUN"

    if (-not (Test-Path $TunDir)) {
        New-Item -ItemType Directory -Path $TunDir -Force | Out-Null
        Write-Info "Создана директория: $TunDir"
    }

    $tunExePath = Join-Path $TunDir "InvisibleMan-TUN.exe"
    if (Test-Path $tunExePath) {
        Write-Info "InvisibleMan-TUN.exe уже существует, пропуск"
        return
    }

    Write-Info "Получение информации о последнем релизе..."
    try {
        $release = Invoke-RestMethod -Uri $TunRelease -UseBasicParsing
        $asset = $release.assets | Where-Object {
            $_.name -match "windows.*x64|win.*x64|x64.*windows" -or $_.name -match "\.exe$"
        } | Select-Object -First 1

        if (-not $asset) {
            $asset = $release.assets | Where-Object { $_.name -match "windows|win" } | Select-Object -First 1
        }

        if (-not $asset) {
            Write-Err "Не найден подходящий файл в релизе."
            Write-Err "Скачайте вручную: https://github.com/InvisibleManVPN/InvisibleMan-TUN/releases/latest"
            Write-Err "Поместите InvisibleMan-TUN.exe в: $TunDir"
            return
        }

        Write-Info "Скачивание $($asset.name)..."
        $tempFile = Join-Path $env:TEMP $asset.name

        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tempFile -UseBasicParsing

        if ($asset.name -match "\.zip$") {
            Write-Info "Распаковка архива..."
            Expand-Archive -Path $tempFile -DestinationPath $TunDir -Force
            Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
        }
        else {
            Move-Item -Path $tempFile -Destination $tunExePath -Force
        }

        Write-Success "InvisibleMan-TUN -> $TunDir"
    }
    catch {
        Write-Err "Не удалось скачать InvisibleMan-TUN: $_"
        Write-Err "Скачайте вручную: https://github.com/InvisibleManVPN/InvisibleMan-TUN/releases/latest"
    }
}

# ─── Шаг 4: Сборка .NET приложения ───────────────────────────────────────────

function Build-DotNetApp {
    if ($Publish) {
        Write-StepHeader "Шаг 4: Публикация .NET ($Configuration, $Runtime)"
    }
    else {
        Write-StepHeader "Шаг 4: Сборка .NET ($Configuration)"
    }

    if (-not (Test-Path $SolutionFile)) {
        Write-Err "Файл решения не найден: $SolutionFile"
        exit 1
    }

    Push-Location $RootDir
    try {
        Write-Info "Восстановление NuGet-пакетов..."
        & dotnet restore $SolutionFile
        if ($LASTEXITCODE -ne 0) {
            Write-Err "dotnet restore: ошибка (код: $LASTEXITCODE)"
            exit $LASTEXITCODE
        }
        Write-Success "NuGet-пакеты восстановлены"

        if ($Publish) {
            $absOutput = [System.IO.Path]::GetFullPath((Join-Path $RootDir $OutputDir))

            Write-Info "Публикация приложения..."
            & dotnet publish $SolutionFile `
                -c $Configuration `
                -r $Runtime `
                --self-contained true `
                -o $absOutput

            if ($LASTEXITCODE -ne 0) {
                Write-Err "dotnet publish: ошибка (код: $LASTEXITCODE)"
                exit $LASTEXITCODE
            }
            Write-Success "Опубликовано в: $absOutput"
        }
        else {
            Write-Info "Сборка приложения..."
            & dotnet build $SolutionFile -c $Configuration

            if ($LASTEXITCODE -ne 0) {
                Write-Err "dotnet build: ошибка (код: $LASTEXITCODE)"
                exit $LASTEXITCODE
            }
            Write-Success "Приложение собрано ($Configuration)"
        }
    }
    finally {
        Pop-Location
    }
}

# ─── Основной поток ──────────────────────────────────────────────────────────

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ""
Write-Host "  Invisible Gorilla - XRay Client :: Build Script" -ForegroundColor Magenta
Write-Host "  v3.2.5.0" -ForegroundColor DarkGray
Write-Host ""

Test-Prerequisites -BuildStep $Step

switch ($Step) {
    "All" {
        Build-GoWrapper
        Get-GeoFiles
        if (-not $SkipTUN) {
            Get-TunService
        }
        else {
            Write-Info "Шаг 3 (TUN) пропущен (-SkipTUN)"
        }
        Build-DotNetApp
    }
    "GoWrapper" { Build-GoWrapper }
    "GeoFiles"  { Get-GeoFiles }
    "TUN"       { Get-TunService }
    "DotNet"    { Build-DotNetApp }
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed

Write-Host ""
Write-Host ("=" * 60) -ForegroundColor Green
Write-Host ("  Готово за {0:mm\:ss\.ff}" -f $elapsed) -ForegroundColor Green
Write-Host ("=" * 60) -ForegroundColor Green
Write-Host ""
