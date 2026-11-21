[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path $PSScriptRoot '..\.work\launcher'))
$ErrorActionPreference = 'Stop'
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$exe = Join-Path $OutputDirectory 'CairnVRLauncher.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll "/win32manifest:$PSScriptRoot\launcher.manifest" "/out:$exe" "$PSScriptRoot\Launcher.cs"
if ($LASTEXITCODE -ne 0) { throw "C# compiler failed with exit code $LASTEXITCODE" }
Get-Item -LiteralPath $exe
