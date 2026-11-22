Soundboard - sounds straight into your microphone
================================================

Play sounds into your microphone: Discord, in-game voice chat, OBS...
No virtual cable, no device switching.

Requirements: Windows 11 (or a recent Windows 10), 64-bit.
The app is in English, or in French when Windows is in French (see Settings).


BEFORE INSTALLING: Smart App Control
------------------------------------
Soundboard is not code-signed (signing certificates are expensive).
If "Smart App Control" is turned on, Windows silently blocks Soundboard.

To check or turn it off:
  Windows Security > App & browser control
  > Smart App Control settings > Off

Microsoft Defender (the antivirus) stays on.


INSTALL
-------
1. Run Soundboard-Setup.exe.
   If Windows shows "Windows protected your PC":
   click "More info", then "Run anyway".
2. Answer "Yes" to install. A Soundboard shortcut (music note) appears
   on the Desktop and in the Start menu.
3. On first launch, tick the microphone you use and click "Apply".
   Windows asks for administrator permission: this is expected,
   the sound cuts out for about 2 seconds.

The status light at the bottom of the window:
  green  = connected, other people hear your sounds
  orange = no application is using the microphone yet
  red    = module missing (after a driver update): click it > "Repair"


USE
---
- Add sounds: "Add" button (files, folder, pack), drag and drop, or Ctrl+V.
- Click = play, middle click = stop, right click = volume, hotkey, colour...
- In Discord: Settings > Voice & Video > Noise Suppression = "None"
  (Soundboard has its own noise suppression, which never filters the sounds).


ANTI-CHEAT
----------
Soundboard never touches any game (no injection, no memory reading,
no kernel driver). It is a standard Windows audio effect on your microphone,
like Realtek, Nahimic or NVIDIA Broadcast.


UNINSTALL
---------
Windows Settings > Apps > Installed apps > Soundboard > Uninstall.
Your microphone gets its original settings back.
