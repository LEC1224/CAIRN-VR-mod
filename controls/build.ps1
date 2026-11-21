param(
    [string]$BepInExPath = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Cairn\BepInEx",
    [string]$SdkPath,
    [string]$OutputDirectory,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $workspace '.work\controls\package\BepInEx\plugins\CairnVR' }
if (!$SdkPath) { $SdkPath = Join-Path $workspace '.work\controls\OpenXR-SDK' }
$revision = '24a1f2b92d212501a2b1b8d30ffd53abf55fcfa8'
if (!(Test-Path "$SdkPath\include\openxr\openxr.h")) {
    git clone --filter=blob:none --no-checkout https://github.com/KhronosGroup/OpenXR-SDK.git $SdkPath
    if ($LASTEXITCODE) { throw 'Could not download OpenXR headers.' }
    git -C $SdkPath checkout --detach $revision
    if ($LASTEXITCODE) { throw 'Could not check out OpenXR headers.' }
}
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsEnvironment = & $vswhere -latest -products '*' -find 'VC\Auxiliary\Build\vcvars64.bat'
if (!$vsEnvironment) { throw 'The Visual Studio x64 C++ build tools are required.' }
$nativeOut = Join-Path $workspace '.work\controls\native'
$layerDirectory = Join-Path $OutputDirectory 'openxr'
New-Item -ItemType Directory -Force $nativeOut,$layerDirectory | Out-Null
& "$PSScriptRoot\tracking\compile.cmd" $vsEnvironment "$SdkPath\include" $nativeOut
if ($LASTEXITCODE) { throw 'OpenXR layer compilation failed.' }
if ($Test) {
    & "$PSScriptRoot\tests\run.cmd" $vsEnvironment "$SdkPath\include" $nativeOut
    if ($LASTEXITCODE) { throw 'OpenXR input test failed.' }
}
Copy-Item "$nativeOut\CairnTracking.dll","$PSScriptRoot\tracking\CairnTracking.json" $layerDirectory -Force
dotnet build "$PSScriptRoot\CairnVR.Controls.csproj" -c Release "-p:BepInExPath=$BepInExPath"
if ($LASTEXITCODE) { throw 'Climbing controls build failed.' }
Copy-Item "$PSScriptRoot\bin\Release\net6.0\CairnVR.Controls.dll" $OutputDirectory -Force
