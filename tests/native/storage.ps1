param([Parameter(Mandatory = $true)][string] $Irx)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$exe = (Resolve-Path $Irx).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('irx-storage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$oldCache = $env:IRX_CACHE_DIR
$oldData = $env:IRX_DATA_DIR
Push-Location $root
try {
    $env:IRX_CACHE_DIR = Join-Path $root 'cache'
    $env:IRX_DATA_DIR = Join-Path $root 'data'
    @'
LIBRARY XCRDAPI.dll
EXPORTS
    DStorageGetFactory
'@ | Set-Content -Encoding ASCII storage.def
    @'
LIBRARY xmem.dll
EXPORTS
    XMemVirtualAlloc
    XMemAllocatePhysicalPages
    XMemFreePhysicalPages
    XMemMapPhysicalPages
    XMemMapPhysicalPagesScatter
'@ | Set-Content -Encoding ASCII xmem.def
    & lib.exe /nologo /machine:x64 /def:storage.def /out:storage.lib
    if ($LASTEXITCODE -ne 0) { throw 'DirectStorage import library generation failed.' }
    & lib.exe /nologo /machine:x64 /def:xmem.def /out:xmem.lib
    if ($LASTEXITCODE -ne 0) { throw 'XMem import library generation failed.' }
    [System.IO.File]::WriteAllBytes((Join-Path $root 'payload.bin'), [byte[]](1..8))
    $source = Join-Path $PSScriptRoot 'storage.c'
    & cl.exe /nologo /W4 /WX /wd4055 /wd4191 /O2 /GS- /Zl $source /link /NODEFAULTLIB /ENTRY:ProbeEntry /SUBSYSTEM:CONSOLE /BASE:0x600000000 /DYNAMICBASE /FIXED:NO /OUT:probe.exe storage.lib xmem.lib kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed: $LASTEXITCODE" }
    & $exe run (Join-Path $root 'probe.exe')
    if ($LASTEXITCODE -ne 42) { throw "DirectStorage probe failed: $LASTEXITCODE (see storage.c for failure stage)" }
    Write-Host 'DirectStorage factory, queues, raw/zlib reads, virtual drive, physical-page writes and ordered error status passed.'
}
finally {
    Pop-Location
    $env:IRX_CACHE_DIR = $oldCache
    $env:IRX_DATA_DIR = $oldData
    Remove-Item -Recurse -Force $root
}

# Translate the validated guest sentinel into ordinary test success.
exit 0
