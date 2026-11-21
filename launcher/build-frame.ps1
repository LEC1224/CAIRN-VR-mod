[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path $PSScriptRoot '..\.work\launcher'))
$ErrorActionPreference = 'Stop'
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$compiler = Get-Command x86_64-w64-mingw32-gcc -ErrorAction Stop
$exe = Join-Path $OutputDirectory 'CairnVRFrame.exe'
& $compiler.Source -std=c11 -Os -s -static -Wall -Wextra -Werror -mwindows -o $exe (Join-Path $PSScriptRoot 'FrameBootstrap.c') -luser32
if ($LASTEXITCODE -ne 0) { throw "Frame bootstrap compile failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'cairn-frame.sh') -Destination $OutputDirectory -Force
Get-Item -LiteralPath $exe
