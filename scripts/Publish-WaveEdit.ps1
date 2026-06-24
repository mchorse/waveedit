<#
.SYNOPSIS
  Publishes WaveEdit as a self-contained, single-file standalone into the per-user
  install folder, copies the document icon next to it, and re-points the .wav file
  association at the install. No administrator rights required.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\Publish-WaveEdit.ps1
  # custom location / skip re-registering the association:
  powershell -ExecutionPolicy Bypass -File scripts\Publish-WaveEdit.ps1 -InstallDir "D:\Apps\WaveEdit" -NoRegister
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\WaveEdit'),
    [switch]$NoRegister
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'AudioEditor\AudioEditor.csproj'

# Close any running instance so the exe isn't locked.
Get-Process WaveEdit -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Publishing WaveEdit to $InstallDir ..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Sidecar document icon used by the .wav file-type association.
Copy-Item (Join-Path $repo 'AudioEditor\wav-document.ico') (Join-Path $InstallDir 'wav-document.ico') -Force

# Third-party license notices.
$notices = Join-Path $repo 'THIRD-PARTY-NOTICES.txt'
if (Test-Path $notices) { Copy-Item $notices (Join-Path $InstallDir 'THIRD-PARTY-NOTICES.txt') -Force }

if (-not $NoRegister) {
    & (Join-Path $PSScriptRoot 'Register-WaveEditWav.ps1') -ExePath (Join-Path $InstallDir 'WaveEdit.exe')
    try { ie4uinit.exe -ClearIconCache 2>$null } catch {}
}

Write-Host "Done. Installed: $(Join-Path $InstallDir 'WaveEdit.exe')" -ForegroundColor Green
