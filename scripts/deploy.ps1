# Builds the single-file Soundboard.exe, packages it for sharing and installs / updates it on this PC.
#   dist\Soundboard-Setup.exe          - the installer (the exe installs itself)
#   dist\Soundboard-<version>.zip      - installer + README.txt (English) + LISEZ-MOI.txt (French)
#   this PC: the exe installs itself (%LOCALAPPDATA%\Programs\Soundboard, shortcuts,
#            entry in Windows "Installed apps"), closes the running copy and restarts.
# Run scripts\build.ps1 first (it builds bin\SoundboardApo.dll, which the exe embeds).
#   -NoInstall  only build and package (used by the release workflow)
#   -Version    version stamped into the exe (default: the one in app\Soundboard.csproj)
param([switch]$NoInstall, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path

if (-not (Test-Path (Join-Path $root 'bin\SoundboardApo.dll'))) { throw "bin\SoundboardApo.dll is missing: run scripts\build.ps1 first." }
$project = Join-Path $root 'app\Soundboard.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }

$out = Join-Path $root 'bin\single'
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o $out --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$built = Join-Path $out 'Soundboard.exe'

# ---- package
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist 'Soundboard-Setup.exe'
Copy-Item $built $setup -Force
$readmes = 'README.txt', 'LISEZ-MOI.txt' | ForEach-Object { Join-Path $root "packaging\$_" }
$zip = Join-Path $dist "Soundboard-$Version.zip"
Compress-Archive -Path (@($setup) + $readmes) -DestinationPath $zip
Write-Host "Package ($Version): $dist" -ForegroundColor Green

# ---- install / update on this PC
if (-not $NoInstall) {
    # WaitForExit, not -Wait: -Wait would also wait for the Soundboard it restarts
    $p = Start-Process $built -ArgumentList '--install', '--silent' -PassThru
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "Install failed (exit code $($p.ExitCode))." }
    Write-Host "Installed and restarted: $env:LOCALAPPDATA\Programs\Soundboard\Soundboard.exe" -ForegroundColor Green
}
