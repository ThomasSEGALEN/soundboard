# Shared helpers for install.ps1 / uninstall.ps1
$ErrorActionPreference = 'Stop'

$Script:ApoClsid    = '{7C3E5A21-9B4D-4F6E-A1C2-5D8E3F907B14}'
$Script:InstallDir  = Join-Path $env:ProgramFiles 'Soundboard'
$Script:DllTarget   = Join-Path $Script:InstallDir 'SoundboardApo.dll'
$Script:BackupDir   = Join-Path $env:ProgramData 'Soundboard\backup'
$Script:CaptureRoot = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'

# Endpoint property keys
$Script:KeySfxComposite = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13'  # PKEY_CompositeFX_StreamEffectClsid (REG_MULTI_SZ)
$Script:KeySfxLegacy    = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5'   # PKEY_FX_StreamEffectClsid (REG_SZ)
$Script:KeyEfxComposite = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15'  # PKEY_CompositeFX_EndpointEffectClsid (REG_MULTI_SZ)
$Script:KeyEfxLegacy    = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'   # PKEY_FX_EndpointEffectClsid (REG_SZ)
$Script:KeyEfxModes     = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'   # PKEY_EFX_ProcessingModes_Supported_For_Streaming
$Script:KeySfxModes     ='{d3993a3f-99c2-4402-b5ec-a92a0367664b},5'   # PKEY_SFX_ProcessingModes_Supported_For_Streaming (REG_MULTI_SZ)
$Script:SfxModes        = [string[]]@(
    '{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}',  # DEFAULT
    '{98951333-B9CD-48B1-A0A3-FF40682D73F7}',  # COMMUNICATIONS (Discord, Teams…)
    '{FC1CFC9B-B9D6-4CFA-B5E0-4BB2166878B2}',  # SPEECH
    '{9E90EA20-B493-4FD1-A1A8-7E1361A956CF}')  # RAW (Discord opens the mic in raw mode)
$Script:KeyDisableSysFx ='{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'   # PKEY_AudioEndpoint_Disable_SysFx
$Script:KeyFriendly     = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'
$Script:KeyDeviceDesc   = '{b3f8fa53-0004-438e-9003-51a46e139bfc},6'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script as administrator (right click > Run as administrator)."
    }
}

# Admins only have SetValue+ReadKey on these keys, so never ask for more.
function Open-ValueKey([string]$path) {
    [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        $path,
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
        [System.Security.AccessControl.RegistryRights]'SetValue, ReadKey')
}

function Get-Microphones {
    $root = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($Script:CaptureRoot)
    foreach ($id in $root.GetSubKeyNames()) {
        $ep = $root.OpenSubKey($id)
        $props = $ep.OpenSubKey('Properties')
        $fx = $ep.OpenSubKey('FxProperties')
        [pscustomobject]@{
            Id       = $id
            Name     = if ($props) { "$($props.GetValue($Script:KeyFriendly)) ($($props.GetValue($Script:KeyDeviceDesc)))" } else { $id }
            Active   = ($ep.GetValue('DeviceState') -band 0xF) -eq 1
            HasFx    = $null -ne $fx
        }
    }
}

function Read-Value($key, [string]$name) {
    if ($null -eq $key -or -not ($key.GetValueNames() -contains $name)) { return $null }
    [pscustomobject]@{
        Kind  = $key.GetValueKind($name).ToString()
        Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
}

# AudioEndpointBuilder caches endpoint properties; restarting it also restarts Audiosrv.
function Restart-AudioService {
    Write-Host "Restarting the audio services…"
    Restart-Service -Name AudioEndpointBuilder -Force
    Start-Service -Name Audiosrv
}
