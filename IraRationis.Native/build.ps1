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
$test = Join-Path $PSScriptRoot 'DecoderTests.cpp'
$memoryAccessContracts = Join-Path $PSScriptRoot 'MemoryAccessQueryTests.cpp'
$script = Join-Path $OutputDirectory 'build-native.cmd'
# Embed compiler symbols in objects and link a self-contained PDB for profiling.
# /DEBUG changes linker defaults, so retain release optimization explicitly.
@"
@echo off
call "$vcvars" >nul || exit /b 1
cd /d "$OutputDirectory" || exit /b 1
cl /nologo /std:c++20 /W4 /WX /O2 /MT /EHsc "$test" /Fe:decoder-tests.exe || exit /b 1
decoder-tests.exe || exit /b 1
cl /nologo /std:c++20 /W4 /WX /O2 /MT /EHsc "$memoryAccessContracts" /Fe:memory-access-query-tests.exe || exit /b 1
memory-access-query-tests.exe || exit /b 1
cl /nologo /std:c++20 /W4 /WX /O2 /Z7 /MT /EHsc /LD "$source" /link /OUT:irx-native.dll /DEBUG:FULL /OPT:REF,ICF /INCREMENTAL:NO /PDB:irx-native.pdb || exit /b 1
"@ | Set-Content -Encoding ASCII $script
& cmd.exe /d /c "`"$script`""
if ($LASTEXITCODE -ne 0) { throw "Native component build failed ($LASTEXITCODE)." }
