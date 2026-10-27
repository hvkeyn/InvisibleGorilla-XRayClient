<#
.SYNOPSIS
Collects every VLESS outbound from a folder of xray config JSONs into one note.

.EXAMPLE
.\scripts\collect-vless.ps1 -ConfigDir "C:\path\to\Configs" -OutFile ".\notes\vless-keys.md"
#>
param(
    [Parameter(Mandatory = $true)][string]$ConfigDir,
    [string]$OutFile = ".\notes\vless-keys.md",
    [int]$TimeoutMs = 1200,
    [switch]$SkipProbe
)

$ErrorActionPreference = "Stop"

function New-VlessLink {
    param($Outbound, $Remark)

    $vnext = $Outbound.settings.vnext | Select-Object -First 1
    if (-not $vnext) { return $null }
    $user = $vnext.users | Select-Object -First 1
    if (-not $user) { return $null }

    $ss = $Outbound.streamSettings
    $security = "none"
    if ($ss.security) { $security = $ss.security }
    $network = "tcp"
    if ($ss.network) { $network = $ss.network }

    $query = [ordered]@{
        type       = $network
        security   = $security
        encryption = "none"
    }
    if ($user.flow) { $query.flow = $user.flow }

    if ($ss.realitySettings) {
        foreach ($p in @("serverName", "publicKey", "shortId", "fingerprint", "spiderX")) {
            $value = $ss.realitySettings.$p
            if ($value) { $query[$p] = $value }
        }
    }
    if ($ss.tlsSettings) {
        foreach ($p in @("serverName", "fingerprint")) {
            $value = $ss.tlsSettings.$p
            if ($value) { $query[$p] = $value }
        }
    }
    if ($ss.xhttpSettings) {
        if ($ss.xhttpSettings.path) { $query.path = $ss.xhttpSettings.path }
        if ($ss.xhttpSettings.host) { $query.host = $ss.xhttpSettings.host }
        if ($ss.xhttpSettings.mode) { $query.mode = $ss.xhttpSettings.mode }
    }
    if ($ss.wsSettings) {
        if ($ss.wsSettings.path) { $query.path = $ss.wsSettings.path }
        if ($ss.wsSettings.headers.Host) { $query.host = $ss.wsSettings.headers.Host }
    }
    if ($ss.grpcSettings -and $ss.grpcSettings.serviceName) { $query.serviceName = $ss.grpcSettings.serviceName }

    $pairs = ($query.GetEnumerator() | Where-Object { $_.Value } | ForEach-Object { "{0}={1}" -f $_.Key, [uri]::EscapeDataString([string]$_.Value) }) -join "&"
    $label = [uri]::EscapeDataString($Remark)
    return "vless://$($user.id)@$($vnext.address):$($vnext.port)?$pairs#$label"
}

$files = Get-ChildItem -Path $ConfigDir -Recurse -File -Filter *.json | Where-Object { $_.Length -gt 10 }

$working = New-Object System.Collections.ArrayList
$dead = New-Object System.Collections.ArrayList

foreach ($file in $files) {
    $json = $null
    try { $json = Get-Content $file.FullName -Raw | ConvertFrom-Json } catch { continue }
    if (-not $json.outbounds) { continue }

    $outbound = $json.outbounds | Where-Object { $_.protocol -eq "vless" } | Select-Object -First 1
    if (-not $outbound) { continue }

    $remark = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $link = New-VlessLink -Outbound $outbound -Remark $remark
    if (-not $link) { continue }

    $vnext = $outbound.settings.vnext | Select-Object -First 1
    $alive = $true
    $rtt = -1
    if (-not $SkipProbe) {
        $alive = $false
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $task = $client.ConnectAsync($vnext.address, [int]$vnext.port)
            if ($task.Wait($TimeoutMs) -and $client.Connected) { $alive = $true; $rtt = $sw.ElapsedMilliseconds }
            $client.Close()
        } catch { $alive = $false }
    }

    $entry = New-Object psobject -Property @{
        Remark = $remark; Host = $vnext.address; Port = $vnext.port; Rtt = $rtt; Link = $link; File = $file.Name
    }
    if ($alive) { [void]$working.Add($entry) } else { [void]$dead.Add($entry) }
}

$working = $working | Sort-Object Rtt
$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$lines = New-Object System.Collections.ArrayList
[void]$lines.Add("# VLESS keys")
[void]$lines.Add("")
[void]$lines.Add("Source: $ConfigDir")
[void]$lines.Add("Collected: $(Get-Date -Format 'yyyy-MM-dd HH:mm') | working: $($working.Count) | unreachable: $($dead.Count)")
[void]$lines.Add("")
[void]$lines.Add("## Working")
[void]$lines.Add("")
if (-not $working -or $working.Count -eq 0) { [void]$lines.Add("_none_") }
foreach ($w in $working) {
    [void]$lines.Add("### $($w.Remark) ($($w.Host):$($w.Port), $($w.Rtt) ms)")
    [void]$lines.Add('```')
    [void]$lines.Add($w.Link)
    [void]$lines.Add('```')
    [void]$lines.Add("")
}
[void]$lines.Add("## Unreachable")
[void]$lines.Add("")
if (-not $dead -or $dead.Count -eq 0) { [void]$lines.Add("_none_") }
foreach ($d in $dead) { [void]$lines.Add("- $($d.Remark) - $($d.Host):$($d.Port)") }

$lines | Set-Content -Path $OutFile -Encoding UTF8

Write-Host "Working VLESS: $($working.Count); unreachable: $($dead.Count)"
Write-Host "Note: $OutFile"