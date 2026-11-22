<#
.SYNOPSIS
  Builds the InputFlip installer: runs the tests, publishes a Release build that carries .NET and the
  Windows App SDK (nothing else to install), and packs it with Velopack.

.DESCRIPTION
  Output, both git-ignored:
    publish\win-x64\                  the app folder (runs as is, too)
    releases\InputFlip-Setup.exe      the installer to give to people (a copy of Velopack's SwitchboardApp-win-Setup.exe)
    releases\InputFlip-Portable.zip   the same app without installing
    releases\*.nupkg, releases.*.json what the Update button reads

  releases\ is also where the installed app's Update button looks (the path is baked into the build; settings.json's
  "updateSource" overrides it). So on this PC: bump <Version>, run this script, click Update in the app.

  The installer puts InputFlip in %LOCALAPPDATA%\SwitchboardApp and adds Start menu and desktop shortcuts. The pack id
  is still "SwitchboardApp" (the app's name before 1.1) so installed copies keep updating; Velopack only updates an
  install with the same pack id. Settings live elsewhere (%LOCALAPPDATA%\InputFlip, or %LOCALAPPDATA%\Switchboard on
  PCs that had 1.0), because Velopack removes its whole install folder on uninstall.

  Not signed: Windows SmartScreen warns on first run ("More info", then "Run anyway").

  Needs the .NET 10 SDK and the vpk tool, same version as the Velopack package in InputFlip.csproj:
    dotnet tool install -g vpk --version 1.2.161

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
#>
param(
    # Defaults to <Version> in src\InputFlip\InputFlip.csproj.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\InputFlip\InputFlip.csproj'
$publish = Join-Path $repo 'publish\win-x64'
$releases = Join-Path $repo 'releases'

if (-not $Version) {
    [xml]$xml = Get-Content $project
    $Version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}
if (-not $Version) { throw "No version given and none found in $project." }

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk isn't installed. Run: dotnet tool install -g vpk --version 1.2.161"
}

function Run([string]$what, [scriptblock]$command) {
    Write-Host "== $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

Push-Location $repo
try {
    Run 'Tests' { dotnet test --nologo -v q }

    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    Run "Publish $Version (Release, self-contained, win-x64)" {
        # UpdateSource: the app's update button looks in this releases folder (settings.json's updateSource overrides it).
        dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained -p:Version=$Version "-p:UpdateSource=$releases" -o $publish --nologo
    }

    # The Windows App SDK brings its on-device AI runtime (onnxruntime, DirectML, Microsoft.Windows.AI.*), about
    # 42 MB that InputFlip never loads. Checked on 2026-11-21: the app runs without it (main window, setup,
    # every settings page). If a future Windows App SDK fails to start without them, delete these lines.
    $unused = Get-ChildItem $publish -File | Where-Object { $_.Name -match '^(onnxruntime|DirectML)\.dll$|^Microsoft\.Windows\.AI\.|^Microsoft\.ML\.' }
    Write-Host "== Leaving out $($unused.Count) unused AI runtime files ($([math]::Round(($unused | Measure-Object Length -Sum).Sum / 1MB)) MB)" -ForegroundColor Cyan
    $unused | Remove-Item

    # vpk refuses to pack a version that's already in releases\, so a second build of the same version would fail.
    # Start from an empty folder: the installed app's Update button reads it and only needs the newest full package.
    # Bump <Version> in InputFlip.csproj for each build you want the button to offer.
    if (Test-Path $releases) { Remove-Item -Recurse -Force $releases }

    Run 'Pack with Velopack' {
        vpk pack `
            --packId SwitchboardApp `
            --packVersion $Version `
            --packDir $publish `
            --runtime win-x64 `
            --mainExe InputFlip.exe `
            --packTitle InputFlip `
            --packAuthors "InputFlip contributors" `
            --icon (Join-Path $repo 'src\InputFlip\Assets\InputFlip.ico') `
            --outputDir $releases `
            --skip-updates
    }

    # Friendlier names for the files people download. The originals stay: Velopack's update needs only the
    # .nupkg and releases.*.json, but keeping them makes the folder match what vpk expects next time.
    Copy-Item (Join-Path $releases 'SwitchboardApp-win-Setup.exe') (Join-Path $releases 'InputFlip-Setup.exe')
    Copy-Item (Join-Path $releases 'SwitchboardApp-win-Portable.zip') (Join-Path $releases 'InputFlip-Portable.zip')

    Write-Host ''
    Write-Host "Installer: $(Join-Path $releases 'InputFlip-Setup.exe')" -ForegroundColor Green
}
finally {
    Pop-Location
}
