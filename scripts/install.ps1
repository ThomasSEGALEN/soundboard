# Installs the Soundboard APO on one or more microphones.
# Does NOT change default devices or create any virtual device: it adds a processing
# step on the existing microphone, the same mechanism Windows uses for mic effects.
# It is installed as an endpoint effect (EFX), the only slot Windows applies to every
# stream of the mic, including the RAW streams used by Discord.
# Everything it changes is backed up to %ProgramData%\Soundboard\backup and undone by uninstall.ps1.
param(
    [string]$DllPath = (Join-Path $PSScriptRoot '..\bin\SoundboardApo.dll'),
    [string[]]$EndpointIds
)
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Admin

$DllPath = (Resolve-Path $DllPath).Path
$MS = [Microsoft.Win32.RegistryValueKind]::MultiString
$SZ = [Microsoft.Win32.RegistryValueKind]::String

# ---- pick microphones
$mics = @(Get-Microphones | Where-Object Active)
if (-not $EndpointIds) {
    Write-Host "`nMicros actifs :"
    for ($i = 0; $i -lt $mics.Count; $i++) {
        $note = if ($mics[$i].HasFx) { '' } else { '  (not supported: no FxProperties key)' }
        Write-Host ("  [{0}] {1}{2}" -f ($i + 1), $mics[$i].Name, $note)
    }
    $answer = Read-Host "`nMicrophone number(s) to equip (e.g. 1 or 1,2), or A for all"
    if ($answer -match '^[aAtT]') { $EndpointIds = $mics.Id }
    else { $EndpointIds = $answer -split '[,; ]+' | Where-Object { $_ } | ForEach-Object { $mics[[int]$_ - 1].Id } }
}
$targets = @($mics | Where-Object { $EndpointIds -contains $_.Id })
if ($targets.Count -eq 0) { throw "No microphone selected." }

function Restore-FromBackup($key, [string]$name, $saved) {
    if ($null -eq $saved) {
        if ($key.GetValueNames() -contains $name) { $key.DeleteValue($name) }
    } else {
        $kind = [Microsoft.Win32.RegistryValueKind]$saved.Kind
        $value = if ($kind -eq 'MultiString') { [string[]]@($saved.Value) } else { $saved.Value }
        $key.SetValue($name, $value, $kind)
    }
}

# ---- copy + register the DLL (audio service stopped so an older copy is not locked)
Write-Host "`nStopping the audio service…"
Stop-Service -Name Audiosrv -Force
try {
    New-Item -ItemType Directory -Force $Script:InstallDir | Out-Null
    $logDir = Join-Path $env:ProgramData 'Soundboard\logs'
    New-Item -ItemType Directory -Force $logDir | Out-Null
    icacls $logDir /grant '*S-1-1-0:(OI)(CI)M' /Q | Out-Null   # audiodg runs as LOCAL SERVICE
    Copy-Item $DllPath $Script:DllTarget -Force
    $p = Start-Process regsvr32.exe -ArgumentList '/s', "`"$($Script:DllTarget)`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "regsvr32 failed (exit code $($p.ExitCode))." }

    New-Item -ItemType Directory -Force $Script:BackupDir | Out-Null
    foreach ($mic in $targets) {
        if (-not $mic.HasFx) { Write-Warning "Skipped (no FxProperties key): $($mic.Name)"; continue }
        $fx    = Open-ValueKey "$($Script:CaptureRoot)\$($mic.Id)\FxProperties"
        $props = Open-ValueKey "$($Script:CaptureRoot)\$($mic.Id)\Properties"

        # Backup of the original state (taken once, so re-installing never loses it).
        # Older backups lack some fields: a missing field means "value did not exist".
        $backupFile = Join-Path $Script:BackupDir "$($mic.Id).json"
        if (-not (Test-Path $backupFile)) {
            [pscustomobject]@{
                Id           = $mic.Id
                Name         = $mic.Name
                Composite    = Read-Value $fx $Script:KeySfxComposite
                Legacy       = Read-Value $fx $Script:KeySfxLegacy
                Modes        = Read-Value $fx $Script:KeySfxModes
                EfxComposite = Read-Value $fx $Script:KeyEfxComposite
                EfxLegacy    = Read-Value $fx $Script:KeyEfxLegacy
                EfxModes     = Read-Value $fx $Script:KeyEfxModes
                SysFx        = Read-Value $props $Script:KeyDisableSysFx
            } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $backupFile
        }
        $b = Get-Content $backupFile -Raw | ConvertFrom-Json

        # Undo the stream-effect (SFX) placement used by earlier versions.
        $sfx = Read-Value $fx $Script:KeySfxComposite
        if ($sfx -and (@($sfx.Value) -contains $Script:ApoClsid)) { Restore-FromBackup $fx $Script:KeySfxComposite $b.Composite }
        $sfxl = Read-Value $fx $Script:KeySfxLegacy
        if ($sfxl -and $sfxl.Value -eq $Script:ApoClsid) { Restore-FromBackup $fx $Script:KeySfxLegacy $b.Legacy }
        if ($b.PSObject.Properties.Name -contains 'Modes') { Restore-FromBackup $fx $Script:KeySfxModes $b.Modes }
        elseif (Read-Value $fx $Script:KeySfxModes) { $fx.DeleteValue($Script:KeySfxModes) }

        # Endpoint effect: keep any existing endpoint effects and append ours.
        $list = [System.Collections.Generic.List[string]]::new()
        $existing = Read-Value $fx $Script:KeyEfxComposite
        $legacy   = Read-Value $fx $Script:KeyEfxLegacy
        if ($existing) { $existing.Value | ForEach-Object { $list.Add($_) } }
        elseif ($legacy -and $legacy.Value -and $legacy.Value -ne $Script:ApoClsid -and $legacy.Value -notmatch '^\{0{8}-') { $list.Add([string]$legacy.Value) }
        if (-not ($list -contains $Script:ApoClsid)) { $list.Add($Script:ApoClsid) }
        $fx.SetValue($Script:KeyEfxComposite, [string[]]$list, $MS)
        if (-not $legacy) { $fx.SetValue($Script:KeyEfxLegacy, $Script:ApoClsid, $SZ) }

        # Processing modes for the endpoint effect (Windows ignores the effect without this list).
        $modeList = [System.Collections.Generic.List[string]]::new()
        $curModes = Read-Value $fx $Script:KeyEfxModes
        if ($curModes) { $curModes.Value | ForEach-Object { $modeList.Add($_) } }
        foreach ($m in $Script:SfxModes) { if (-not ($modeList -contains $m)) { $modeList.Add($m) } }
        $fx.SetValue($Script:KeyEfxModes, [string[]]$modeList, $MS)

        # Effects must be allowed on this mic ("Audio enhancements" not Off).
        $sys = Read-Value $props $Script:KeyDisableSysFx
        if ($sys -and [int]$sys.Value -ne 0) {
            $props.SetValue($Script:KeyDisableSysFx, 0, [Microsoft.Win32.RegistryValueKind]::DWord)
        }

        Write-Host "Installed on: $($mic.Name)" -ForegroundColor Green
    }
}
finally {
    Restart-AudioService
}

Write-Host "`nDone. Start Soundboard, then open an app that uses the microphone (Discord…)." -ForegroundColor Green
