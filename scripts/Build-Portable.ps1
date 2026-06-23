<#
.SYNOPSIS
  Builds the portable release zip for itch.io (self-contained, single-file exe + readme)
  into dist\WaveEdit-portable-win-x64.zip. No admin, no install, leaves nothing behind.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\Build-Portable.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$repo  = Split-Path $PSScriptRoot -Parent
$proj  = Join-Path $repo 'AudioEditor\AudioEditor.csproj'
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist' }
$stage = Join-Path $OutDir 'WaveEdit-portable'
$zip   = Join-Path $OutDir 'WaveEdit-portable-win-x64.zip'

# Clean staging folder.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Publish a self-contained, single-file exe straight into the staging folder.
Write-Host "Publishing portable build ..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# The portable build is just the exe - drop the association icon / any pdb if present.
Remove-Item (Join-Path $stage 'wav-document.ico') -Force -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $stage '*.pdb') -ErrorAction SilentlyContinue | Remove-Item -Force

# Read the version from the built exe for the readme.
$fv = (Get-Item (Join-Path $stage 'WaveEdit.exe')).VersionInfo.FileVersion
$ver = if ($fv) { $v = [version]$fv; "$($v.Major).$($v.Minor)" } else { '0.1' }

# Write a plain-text readme (ASCII only so Notepad/PS are happy).
$readme = @"
WaveEdit (portable) v$ver
=========================

A native Windows audio editor. No installation required.

HOW TO RUN
  Double-click WaveEdit.exe

Self-contained 64-bit build - does NOT require .NET to be installed. Windows 10/11.
Settings (recent files, last recording device) are stored in %AppData%\WaveEdit.

QUICK KEYS
  Ctrl+O / Ctrl+S    open / save (WAV or OGG)
  Space              play / stop (from the cursor)
  Shift + drag       select a range (repeat to add regions)
  Alt + drag         subtract a range from the selection
  Ctrl+D             deselect all
  F5                 record
  Ctrl + / Ctrl -    playback speed (0.25x .. 5x)
  + / -              zoom        Ctrl+F full,  Ctrl+E to selection
  F1                 full help / about

Enjoy!
"@
Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding ASCII

# Zip it (archive root is the WaveEdit-portable folder).
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done. Portable v$ver -> $zip ($mb MB)" -ForegroundColor Green
