param([Parameter(Mandatory = $true)][string] $Irx)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$exe = (Resolve-Path $Irx).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('irx-input-memory-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$oldCache = $env:IRX_CACHE_DIR
$oldData = $env:IRX_DATA_DIR
Push-Location $root
try {
    $env:IRX_CACHE_DIR = Join-Path $root 'cache'
    $env:IRX_DATA_DIR = Join-Path $root 'data'
    @'
LIBRARY GameInput.dll
EXPORTS
    GameInputCreate @1 NONAME
'@ | Set-Content -Encoding ASCII gameinput.def
    @'
LIBRARY xmem.dll
EXPORTS
    XMemVirtualAlloc
    XMemAllocatePhysicalPages
    XMemFreePhysicalPages
    XMemMapPhysicalPages
    XMemMapPhysicalPagesScatter
'@ | Set-Content -Encoding ASCII xmem.def
    & lib.exe /nologo /machine:x64 /def:gameinput.def /out:gameinput.lib
    if ($LASTEXITCODE -ne 0) { throw 'GameInput import library generation failed.' }
    & lib.exe /nologo /machine:x64 /def:xmem.def /out:xmem.lib
    if ($LASTEXITCODE -ne 0) { throw 'XMem import library generation failed.' }
    $source = Join-Path $PSScriptRoot 'input-memory.c'
    & cl.exe /nologo /W4 /WX /wd4055 /wd4191 /O2 /GS- /Zl $source /link /NODEFAULTLIB /ENTRY:ProbeEntry /SUBSYSTEM:CONSOLE /BASE:0x600000000 /DYNAMICBASE /FIXED:NO /OUT:probe.exe gameinput.lib xmem.lib kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed: $LASTEXITCODE" }
    & $exe run (Join-Path $root 'probe.exe')
    if ($LASTEXITCODE -ne 42) { throw "Input/memory probe failed: $LASTEXITCODE (see input-memory.c for failure stage)" }
    Write-Host 'GameInput ordinal, input vtables, physical-page aliasing and scatter remapping passed.'
}
finally {
    Pop-Location
    $env:IRX_CACHE_DIR = $oldCache
    $env:IRX_DATA_DIR = $oldData
    Remove-Item -Recurse -Force $root
}
