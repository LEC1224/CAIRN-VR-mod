param(
    [string]$GameDir = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Cairn",
    [string]$SdkDir = (Join-Path $PSScriptRoot '../.work/stereo/sdk'),
    [string]$OutDir = (Join-Path $PSScriptRoot '../.work/stereo/plugin')
)
$ErrorActionPreference='Stop'
$OutDir=[IO.Path]::GetFullPath($OutDir)
$SdkDir=[IO.Path]::GetFullPath($SdkDir)
if (!(Test-Path "$SdkDir/x64/lib/openxr_loader.lib")) {
    $zip=Join-Path (Split-Path $SdkDir) 'khronos.zip'
    New-Item -ItemType Directory -Force (Split-Path $SdkDir) | Out-Null
    Invoke-WebRequest 'https://github.com/KhronosGroup/OpenXR-SDK-Source/releases/download/release-1.1.65/openxr_loader_windows-1.1.65.zip' -OutFile $zip
    if ((Get-FileHash $zip).Hash -ne '859d7de35b7a968112db770b09ed8bddeed3cb16fd8ae070643a857676bea6fd') { throw 'OpenXR package hash mismatch.' }
    Expand-Archive $zip $SdkDir -Force
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) {throw 'A Visual Studio installation with the x64 C++ compiler is required.'}
$src=Join-Path $PSScriptRoot 'StereoBridge.cpp'
# Use a disposable build directory for the link import library and object file.
$cmdFile=Join-Path $OutDir 'compile.cmd'
@"
@echo off
call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /O2 /MT /LD /I"$SdkDir\include" "$src" /link /OUT:CairnVRStereo.dll "$SdkDir\x64\lib\openxr_loader.lib" d3d11.lib
"@ | Set-Content $cmdFile -Encoding ascii
Push-Location $OutDir
try { & $cmdFile; if ($LASTEXITCODE) {throw 'Stereo bridge build failed.'} } finally { Pop-Location }
dotnet build "$PSScriptRoot/CairnVR.Camera.csproj" -c Release "-p:GameDir=$GameDir" -o "$OutDir/managed"
if ($LASTEXITCODE) {throw 'Camera plugin build failed.'}
$deploy=Join-Path $OutDir 'CairnVR.Camera'
New-Item -ItemType Directory -Force $deploy | Out-Null
Copy-Item "$OutDir/managed/CairnVR.Camera.dll","$OutDir/CairnVRStereo.dll","$SdkDir/x64/bin/openxr_loader.dll" -Destination $deploy -Force
Copy-Item "$SdkDir/share/doc/openxr/LICENSE" -Destination "$deploy/LICENSE-OpenXR.txt" -Force
Write-Output $deploy

