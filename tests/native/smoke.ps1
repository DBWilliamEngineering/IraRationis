param(
    [Parameter(Mandatory = $true)]
    [string] $Irx
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$exe = (Resolve-Path -LiteralPath $Irx).Path
. (Join-Path $PSScriptRoot 'Initialize-Msvc.ps1')
$msvc = $null
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('irx-probe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$oldCache = $env:IRX_CACHE_DIR
$oldData = $env:IRX_DATA_DIR
Push-Location $root
try {
    $msvc = Enter-IrxMsvcEnvironment
    $env:IRX_CACHE_DIR = Join-Path $root 'cache'
    $env:IRX_DATA_DIR = Join-Path $root 'data'
    $source = Join-Path $PSScriptRoot 'probe.c'
    & $msvc.Tools.Cl /nologo /W4 /WX /O2 /GS- /Zl $source /link /NODEFAULTLIB /ENTRY:ProbeEntry /SUBSYSTEM:CONSOLE /BASE:0x600000000 /DYNAMICBASE /FIXED:NO /OUT:probe.exe kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed: $LASTEXITCODE" }

    $probe = Join-Path $root 'probe.exe'
    & $exe run $probe --bind-only
    if ($LASTEXITCODE -ne 0) { throw "Bind-only probe failed: $LASTEXITCODE" }

    & $exe run $probe
    if ($LASTEXITCODE -ne 42) { throw "Native probe failed: expected 42, got $LASTEXITCODE" }

    Write-Host 'Native entry point and intercepted thread probe passed.'
}
finally {
    if ($msvc) { Restore-IrxMsvcEnvironment -Previous $msvc.Previous }
    Pop-Location
    $env:IRX_CACHE_DIR = $oldCache
    $env:IRX_DATA_DIR = $oldData
    Remove-Item -Recurse -Force $root
}

# The guest's expected sentinel is not the test process exit code.
# Only reached after every assertion and cleanup has succeeded.
exit 0
