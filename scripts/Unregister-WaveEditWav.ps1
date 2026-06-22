<#
.SYNOPSIS
  Removes the current-user WaveEdit .wav registration created by
  Register-WaveEditWav.ps1. Safe to run even if some keys are missing.

  Note: if you set WaveEdit as the default app for .wav, Windows stored that
  choice separately (UserChoice). This script clears WaveEdit's registration;
  Windows will fall back to another handler. You can also pick a new default in
  Settings > Apps > Default apps.
#>
$ErrorActionPreference = 'SilentlyContinue'

# remove the "Open with" ProgID listing for .wav
Remove-ItemProperty -Path 'HKCU:\Software\Classes\.wav\OpenWithProgids' -Name 'WaveEdit.wav'

# remove the ProgID, application registration and capabilities
Remove-Item -Path 'HKCU:\Software\Classes\WaveEdit.wav'              -Recurse -Force
Remove-Item -Path 'HKCU:\Software\Classes\Applications\WaveEdit.exe' -Recurse -Force
Remove-Item -Path 'HKCU:\Software\WaveEdit'                          -Recurse -Force
Remove-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'WaveEdit'

# refresh shell icons
Add-Type -Namespace Win32 -Name Shell -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, int flags, System.IntPtr a, System.IntPtr b);
"@
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "Unregistered WaveEdit for .wav (current user)." -ForegroundColor Yellow
Write-Host "If WaveEdit was your default, set a new default in Settings > Default apps."
