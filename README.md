# Soundboard

**A Windows soundboard that plays straight into your real microphone.**
No virtual audio cable, no device switching: Discord, in-game voice chat, OBS and every
other app that uses your mic hear your sounds mixed with your voice.

> The app is available in English and French: it follows the Windows language, and you can change it in *Settings → General → Language*.

## Features

- **Plays into the mic itself.** A standard Windows audio effect (APO) on your existing
  microphone mixes the sounds into your voice before any app receives it.
- **Anti-cheat safe.** Nothing is injected into games or Discord: no hooks, no memory
  reading, no kernel driver. It is the same mechanism as Realtek, Nahimic or NVIDIA Broadcast effects.
- **Built-in noise suppression** ([RNNoise](https://github.com/xiph/rnnoise)): it cleans your voice
  *before* the sounds are added, so the sounds are never filtered out.
- **Global hotkeys**, push-to-talk for sounds, and a **keys mode** to assign hotkeys to many sounds in a row.
- **Categories** in a side bar, favourites, search, colours, drag-and-drop reordering.
- **Per-sound editing**: volume, trim with a waveform view, click behaviour (restart, pause, overlay, stop),
  and a preview that only you hear.
- **Loudness normalisation**, smooth fades on pause and stop, live level meters.
- **Monitoring**: hear the sounds and/or your own cleaned voice in your headset.
- **Packs**: export or import a category as a `.soundboard` file to share with friends.
- **Safety nets**: undo (Ctrl+Z), automatic configuration backups with restore.
- Dark and light themes, start with Windows, tray icon, self-installing single exe.

## Install

1. Download `Soundboard-<version>.zip` (or `Soundboard-Setup.exe`) from the
   [latest release](../../releases/latest).
2. **Turn off Smart App Control** if it is on: *Windows Security → App & browser control →
   Smart App Control settings → Off*. Soundboard is not code-signed, and Smart App Control
   silently blocks unsigned apps. Microsoft Defender stays on.
3. Run `Soundboard-Setup.exe`. If SmartScreen shows *"Windows protected your PC"*, click
   **More info → Run anyway**, then confirm the installation.
4. On first launch, tick the microphone you use and click **Apply**.
   Windows asks for administrator rights once to add the effect; audio cuts out for about two seconds.
5. In Discord, set *Settings → Voice & Video → Noise Suppression* to **None**. Discord's
   suppression removes anything that is not a voice, including your sounds.

The status light at the bottom of the window shows the link to the microphone:
**green**, an app is using the mic and hears the sounds; **orange**, the module is ready but
no app has the mic open; **red**, the module is missing (for example after a driver update).
Click the red light to repair it.

**Uninstall** from *Settings → Apps → Installed apps → Soundboard*. Every microphone is restored
exactly as it was.

Requirements: Windows 11 or a recent Windows 10, 64-bit.

**Updating from SoundMix** (the app's name before its first release): run the new installer. It closes SoundMix,
moves your sounds, categories and settings over, takes over its shortcuts and startup entry, and removes it.
Old `.soundmix` packs can still be imported.

## How it works

```
Soundboard.exe ──(shared memory)──► Soundboard APO ──► your real microphone ──► Discord, games, OBS…
     │                             (runs inside audiodg.exe,
     └──► your headset (monitor)    like any Windows mic effect)
```

- `Soundboard.exe` decodes and mixes the sounds, then writes them to a 48 kHz stereo ring buffer in
  shared memory (`Global\SoundMixApo_Ring_v1`).
- The APO (`SoundboardApo.dll`) is registered as an **endpoint effect (EFX)** on the chosen microphones.
  EFX is the only slot that Windows applies to *every* stream of the mic, including the RAW
  streams Discord uses. The APO denoises the voice (RNNoise), adds the sounds and publishes the cleaned
  voice for the *"Hear my voice"* monitor (`Global\SoundMixApo_Voice_v1`).
- Microphone settings are backed up before any change and restored on uninstall.
- The shared-memory names and the APO's COM class ID still carry the project's original name (SoundMix),
  so microphones equipped by an older version keep working after an update.

## Build from source

Requirements:
- Visual Studio 2022 Build Tools with the **Desktop development with C++** workload and a Windows SDK
- .NET 9 SDK

```powershell
# APO DLL + development build of the app (bin\)
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# Single-file installer in dist\, then install it on this PC
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
# ...or only package it
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -NoInstall
```

Tests (engine, configuration, packs and UI layout; they never touch your real configuration):

```powershell
dotnet run --project tests           # engine and configuration
dotnet run --project tests -- --ui   # + window layout checks
```

The microphone module can also be installed by hand with `scripts\install.ps1` and removed with
`scripts\uninstall.ps1` (run as administrator). The app does the same from
*Settings → Microphone*.

### Command-line options

| Option | Effect |
|---|---|
| `--install [--silent]` | Install or update into `%LOCALAPPDATA%\Programs\Soundboard` |
| `--uninstall-app [--quiet]` | Restore the microphones and remove the app |
| `--portable` | Never offer to install |
| `--tray` | Start hidden in the notification area (used at Windows startup) |
| `--first-run` | Show the welcome screen again |

## Releases

Releases are built by GitHub Actions. Push a version tag to publish one:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

The [release workflow](.github/workflows/release.yml) builds the APO and the single-file exe, stamps the
version from the tag, and attaches `Soundboard-Setup.exe` and `Soundboard-<version>.zip` to a new GitHub release.

## Project layout

| Path | Contents |
|---|---|
| `apo/` | The audio processing object (C++): mixing, RNNoise, shared memory, COM registration |
| `app/` | The soundboard (C# / WinForms, .NET 9): UI, audio engine (NAudio), installer, mic setup |
| `tests/` | Self-checking test runner (engine, config, packs, UI layout) |
| `third_party/rnnoise/` | RNNoise v0.1.1 (BSD), patched for MSVC (`_alloca` instead of VLAs) |
| `scripts/` | Build, packaging, manual APO install/uninstall, icon generation |
| `packaging/` | Readme files shipped in the release zip (English and French) |

## Troubleshooting

- **Nobody hears the sounds**: check the light is green and that Discord's noise suppression is set to *None*.
- **Red light after a Windows or driver update**: the update reset the mic effects. Click the light, then **Repair**.
- **The app does not start at all**: Smart App Control is probably on (see *Install*).
- **Diagnostic log**: `%ProgramData%\Soundboard\logs\apo.log`.
- Apps that bypass Windows audio effects entirely do not hear the sounds. This is rare.

## License

[MIT](LICENSE). Soundboard bundles RNNoise (© Mozilla, Jean-Marc Valin, Xiph.Org Foundation, Mark Borgerding) under the BSD
licence ([third_party/rnnoise/COPYING](third_party/rnnoise/COPYING)), and uses
[NAudio](https://github.com/naudio/NAudio) (MIT).
