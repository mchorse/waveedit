<#
.SYNOPSIS
  Registers WaveEdit as a handler for .wav files for the CURRENT USER (HKCU only,
  no administrator rights). This makes WaveEdit appear in "Open with" and in
  Settings > Default apps, and gives .wav files a document icon when WaveEdit is
  the chosen default.

  It does NOT force WaveEdit to be the default — Windows 10/11 protect that choice.
  After running this, set the default yourself (see the printed instructions).

.NOTES
  Undo everything with Unregister-WaveEditWav.ps1.
#>
[CmdletBinding()]
param(
    # Path to WaveEdit.exe. Auto-detected from the build output if omitted.
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

if (-not $ExePath) {
    $candidates = @(
        "$repo\AudioEditor\bin\Release\net8.0-windows\WaveEdit.exe",
        "$repo\AudioEditor\bin\Debug\net8.0-windows\WaveEdit.exe"
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "WaveEdit.exe not found. Build the project first, or pass -ExePath."
}
$ExePath = (Resolve-Path $ExePath).Path
$exeDir  = Split-Path $ExePath
$docIcon = Join-Path $exeDir 'wav-document.ico'
if (-not (Test-Path $docIcon)) {
    Write-Warning "wav-document.ico not found next to the exe; the file-type icon will fall back to the app icon."
    $docIcon = "$ExePath,0"
}

$progId  = 'WaveEdit.wav'
$cmd     = "`"$ExePath`" `"%1`""

function Set-Default($key, $value) {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-Item -Path $key -Value $value
}

# --- ProgID: the document type WaveEdit owns ---
$pk = "HKCU:\Software\Classes\$progId"
Set-Default $pk 'WAV Audio (WaveEdit)'
New-ItemProperty -Path $pk -Name 'FriendlyTypeName' -Value 'WAV Audio (WaveEdit)' -PropertyType String -Force | Out-Null
Set-Default "$pk\DefaultIcon" $docIcon
Set-Default "$pk\shell\open\command" $cmd

# --- list WaveEdit among the "Open with" choices for .wav ---
$owp = "HKCU:\Software\Classes\.wav\OpenWithProgids"
if (-not (Test-Path $owp)) { New-Item -Path $owp -Force | Out-Null }
New-ItemProperty -Path $owp -Name $progId -Value ([byte[]]@()) -PropertyType None -Force | Out-Null

# --- Application registration (so it shows in the Open-with app list) ---
# DefaultIcon here is what .wav files show when the default was set by picking the
# EXE directly in "Open with" (UserChoice = Applications\WaveEdit.exe). Point it at the
# document icon so that route also yields the document look, not the bare app icon.
$ak = "HKCU:\Software\Classes\Applications\WaveEdit.exe"
Set-Default "$ak\shell\open\command" $cmd
Set-Default "$ak\DefaultIcon" $docIcon
if (-not (Test-Path "$ak\SupportedTypes")) { New-Item -Path "$ak\SupportedTypes" -Force | Out-Null }
New-ItemProperty -Path "$ak\SupportedTypes" -Name '.wav' -Value '' -PropertyType String -Force | Out-Null

# --- Capabilities (so WaveEdit appears in Settings > Default apps) ---
$cap = 'HKCU:\Software\WaveEdit\Capabilities'
if (-not (Test-Path $cap)) { New-Item -Path $cap -Force | Out-Null }
New-ItemProperty -Path $cap -Name 'ApplicationName'        -Value 'WaveEdit' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $cap -Name 'ApplicationDescription' -Value 'Native WAV audio editor' -PropertyType String -Force | Out-Null
if (-not (Test-Path "$cap\FileAssociations")) { New-Item -Path "$cap\FileAssociations" -Force | Out-Null }
New-ItemProperty -Path "$cap\FileAssociations" -Name '.wav' -Value $progId -PropertyType String -Force | Out-Null
$reg = 'HKCU:\Software\RegisteredApplications'
if (-not (Test-Path $reg)) { New-Item -Path $reg -Force | Out-Null }
New-ItemProperty -Path $reg -Name 'WaveEdit' -Value 'Software\WaveEdit\Capabilities' -PropertyType String -Force | Out-Null

# --- tell the shell associations changed so icons refresh ---
Add-Type -Namespace Win32 -Name Shell -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, int flags, System.IntPtr a, System.IntPtr b);
"@
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)  # SHCNE_ASSOCCHANGED

Write-Host "Registered WaveEdit for .wav (current user)." -ForegroundColor Green
Write-Host "  exe : $ExePath"
Write-Host "  icon: $docIcon"
Write-Host ""
Write-Host "To make it the DEFAULT (Windows won't let a script do this):" -ForegroundColor Cyan
Write-Host "  1. Right-click any .wav > 'Open with' > 'Choose another app'."
Write-Host "  2. Pick WaveEdit, check 'Always use this app', click OK."
Write-Host "     (or: Settings > Apps > Default apps > search '.wav' > choose WaveEdit)"
Write-Host "  Icons may take a moment (or an Explorer restart) to refresh."
