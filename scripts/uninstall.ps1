# Restores every microphone exactly as it was before install.ps1, then removes the APO.
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Admin

function Restore-Value($key, [string]$name, $saved) {
    if ($null -eq $saved) {
        if ($key.GetValueNames() -contains $name) { $key.DeleteValue($name) }
    } else {
        $kind = [Microsoft.Win32.RegistryValueKind]$saved.Kind
        $value = $saved.Value
        if ($kind -eq 'MultiString') { $value = [string[]]@($value) }
        elseif ($kind -eq 'DWord') { $value = [int]$value }
        $key.SetValue($name, $value, $kind)
    }
}

Write-Host "Stopping the audio service…"
Stop-Service -Name Audiosrv -Force
try {
    if (Test-Path $Script:BackupDir) {
        foreach ($file in Get-ChildItem $Script:BackupDir -Filter '*.json') {
            $b = Get-Content $file.FullName -Raw | ConvertFrom-Json
            $fx    = Open-ValueKey "$($Script:CaptureRoot)\$($b.Id)\FxProperties"
            $props = Open-ValueKey "$($Script:CaptureRoot)\$($b.Id)\Properties"
            if ($fx) {
                Restore-Value $fx $Script:KeySfxComposite $b.Composite
                Restore-Value $fx $Script:KeySfxLegacy $b.Legacy
                Restore-Value $fx $Script:KeySfxModes $b.Modes
                Restore-Value $fx $Script:KeyEfxComposite $b.EfxComposite
                Restore-Value $fx $Script:KeyEfxLegacy $b.EfxLegacy
                Restore-Value $fx $Script:KeyEfxModes $b.EfxModes
            }
            if ($props) { Restore-Value $props $Script:KeyDisableSysFx $b.SysFx }
            Remove-Item $file.FullName
            Write-Host "Restored: $($b.Name)" -ForegroundColor Green
        }
    }

    if (Test-Path $Script:DllTarget) {
        Start-Process regsvr32.exe -ArgumentList '/u', '/s', "`"$($Script:DllTarget)`"" -Wait | Out-Null
        Remove-Item $Script:DllTarget -Force
    }
    if ((Test-Path $Script:InstallDir) -and -not (Get-ChildItem $Script:InstallDir)) { Remove-Item $Script:InstallDir }
}
finally {
    Restart-AudioService
}
Write-Host "`nSoundboard APO uninstalled, microphones restored." -ForegroundColor Green
