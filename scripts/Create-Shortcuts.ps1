<#
.SYNOPSIS
  Creates Start Menu and Desktop shortcuts to the installed WaveEdit.exe.
  Run Publish-WaveEdit.ps1 first so the install folder exists.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\WaveEdit'),
    [switch]$NoDesktop
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallDir 'WaveEdit.exe'
if (-not (Test-Path $exe)) { throw "WaveEdit.exe not found in $InstallDir. Run Publish-WaveEdit.ps1 first." }

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$linkPath) {
    $sc = $shell.CreateShortcut($linkPath)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $InstallDir
    $sc.IconLocation = "$exe,0"
    $sc.Description = "WaveEdit - native WAV audio editor"
    $sc.Save()
    Write-Host "  $linkPath"
}

Write-Host "Created shortcuts:" -ForegroundColor Green
New-Shortcut (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WaveEdit.lnk')
if (-not $NoDesktop) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'WaveEdit.lnk')
}
