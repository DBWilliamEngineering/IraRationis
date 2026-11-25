param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Install Visual Studio with Desktop development with C++.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'MSVC x64 tools are not installed.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$source = Join-Path $PSScriptRoot 'ExceptionHandler.cpp'
$memory = Join-Path $PSScriptRoot 'MemoryProbe.cpp'
$presentation = Join-Path $PSScriptRoot 'PresentationProbe.cpp'
$test = Join-Path $PSScriptRoot 'DecoderTests.cpp'
$script = Join-Path $OutputDirectory 'build-native.cmd'
@"
@echo off
call "$vcvars" >nul || exit /b 1
cd /d "$OutputDirectory" || exit /b 1
cl /nologo /std:c++20 /W4 /WX /O2 /MT /EHsc /LD "$source" "$memory" "$presentation" /link d3d12.lib dxgi.lib d3dcompiler.lib user32.lib /OUT:irx-native.dll || exit /b 1
cl /nologo /std:c++20 /W4 /WX /O2 /MT /EHsc "$test" /Fe:decoder-tests.exe || exit /b 1
decoder-tests.exe || exit /b 1
"@ | Set-Content -Encoding ASCII $script
& cmd.exe /d /c "`"$script`""
if ($LASTEXITCODE -ne 0) { throw "Native component build failed ($LASTEXITCODE)." }
