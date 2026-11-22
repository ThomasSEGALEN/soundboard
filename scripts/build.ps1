# Builds the APO DLL (MSVC) and the soundboard app (.NET) into bin\
#   bin\SoundboardApo.dll  - the microphone module (embedded into Soundboard.exe)
#   bin\app\             - framework-dependent build of the app, for development
# Requirements: Visual Studio 2022 Build Tools (C++ workload + Windows SDK) and the .NET 9 SDK.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

# ---- APO
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio Build Tools not found." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "MSVC C++ tools not found (install the 'Desktop development with C++' workload)." }
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

$apo = Join-Path $root 'apo'
$obj = Join-Path $root 'obj-apo'
New-Item -ItemType Directory -Force $obj | Out-Null
$rn  = Join-Path $root 'third_party\rnnoise'
$rnSrc = 'denoise.c', 'rnn.c', 'rnn_data.c', 'pitch.c', 'kiss_fft.c', 'celt_lpc.c', 'rnn_reader.c' |
         ForEach-Object { "`"$rn\$_`"" }
$rnObj = 'denoise', 'rnn', 'rnn_data', 'pitch', 'kiss_fft', 'celt_lpc', 'rnn_reader' |
         ForEach-Object { "`"$obj\$_.obj`"" }
# RNNoise (C, BSD licence) compiled statically into the APO
$clRn = "cl /nologo /c /O2 /W1 /MT /guard:cf /D_USE_MATH_DEFINES /DRNNOISE_EXPORT= /Fo`"$obj\\`" " + ($rnSrc -join ' ')
# /IMPLIB keeps the import library (.lib/.exp) in obj-apo instead of the current folder
$cl = "cl /nologo /O2 /W4 /EHsc /MT /LD /std:c++17 /guard:cf /DUNICODE /D_UNICODE /DRNNOISE_EXPORT= " +
      "/I`"$rn`" /Fo`"$obj\\`" `"$apo\SoundboardApo.cpp`" " + ($rnObj -join ' ') + " " +
      "/link /DEF:`"$apo\SoundboardApo.def`" /OUT:`"$bin\SoundboardApo.dll`" /IMPLIB:`"$obj\SoundboardApo.lib`" /guard:cf /CETCOMPAT " +
      "ole32.lib advapi32.lib"
cmd /c "`"$vcvars`" >nul && $clRn >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw "APO build failed." }

# ---- App
dotnet publish (Join-Path $root 'app\Soundboard.csproj') -c Release -o (Join-Path $bin 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw "App build failed." }

Write-Host "`nOK -> $bin" -ForegroundColor Green
