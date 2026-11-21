[CmdletBinding()]
param(
    [string]$GameDir = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Cairn",
    [switch]$UseExistingBuild
)
$ErrorActionPreference = 'Stop'
$work = Join-Path $PSScriptRoot '.work'
$dist = Join-Path $PSScriptRoot 'dist'
[void][IO.Directory]::CreateDirectory($work)
[void][IO.Directory]::CreateDirectory($dist)
if (!$UseExistingBuild) {
    & "$PSScriptRoot/launcher/build.ps1"
    & "$PSScriptRoot/launcher/build-frame.ps1"
    & "$PSScriptRoot/camera/build.ps1" -GameDir $GameDir
    & "$PSScriptRoot/controls/build.ps1" -BepInExPath "$GameDir/BepInEx" -Test
    dotnet build "$PSScriptRoot/hud/CairnVR.Hud.csproj" -c Release "-p:GameDir=$GameDir"
    if ($LASTEXITCODE) { throw 'HUD build failed.' }
}
$framework = Join-Path $work 'bepinex-793.zip'
if (!(Test-Path -LiteralPath $framework)) {
    Invoke-WebRequest -UseBasicParsing 'https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip' -OutFile $framework
}
if ((Get-FileHash -LiteralPath $framework).Hash -ne '5B63D7D0E92A2448CCD13B738FF95FF1CC1026C6D718555D4DF8E6C824953AC4') {
    throw 'The downloaded BepInEx archive failed SHA-256 verification.'
}

# Start from the upstream archive, never from a player's modded game directory.
$stage = Join-Path $work ("package-" + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($framework, $stage)
    $ini = Join-Path $stage 'doorstop_config.ini'
    $config = [IO.File]::ReadAllText($ini)
    $enabled = [regex]'(?m)^enabled = true(?=\r?$)'
    if ($enabled.Matches($config).Count -ne 1) { throw 'Unexpected Doorstop configuration.' }
    [IO.File]::WriteAllText($ini, $enabled.Replace($config, 'enabled = false'))

    $plugins = Join-Path $stage 'BepInEx/plugins'
    Copy-Item -LiteralPath "$work/stereo/plugin/CairnVR.Camera", "$work/controls/package/BepInEx/plugins/CairnVR" -Destination $plugins -Recurse
    [void][IO.Directory]::CreateDirectory("$plugins/CairnVR.Hud")
    Copy-Item -LiteralPath "$PSScriptRoot/hud/bin/Release/net6.0/CairnVR.Hud.dll" -Destination "$plugins/CairnVR.Hud"
    Copy-Item -LiteralPath "$work/launcher/CairnVRLauncher.exe", "$work/launcher/CairnVRFrame.exe", "$work/launcher/cairn-frame.sh" -Destination $stage
    Copy-Item -LiteralPath "$PSScriptRoot/README.md" -Destination "$stage/CairnVR-README.md"
    Copy-Item -LiteralPath "$PSScriptRoot/licenses" -Destination "$stage/CairnVR-licenses" -Recurse
    Move-Item -LiteralPath "$stage/changelog.txt" -Destination "$stage/CairnVR-licenses/BepInEx-changelog.txt"
    $temporary = "$stage.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary)
    $output = Join-Path $dist 'CairnVR.zip'
    if ([IO.File]::Exists($output)) { [IO.File]::Replace($temporary, $output, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $output) }
    Get-Item -LiteralPath $output
} finally {
    if ((Test-Path -LiteralPath $stage) -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -eq [IO.Path]::GetFullPath($work)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    if (Test-Path -LiteralPath "$stage.zip") { Remove-Item -LiteralPath "$stage.zip" -Force }
}
