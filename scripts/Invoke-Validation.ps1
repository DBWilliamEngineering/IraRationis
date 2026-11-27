[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/validation/component-checks',
    [switch]$NativeOnly,
    [switch]$UnblockReviewedFixtures
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = Split-Path -Parent $PSScriptRoot

function Invoke-IrxStep {
    param([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$LogPath)
    Write-Host "=== $Name ==="
    # Windows PowerShell represents redirected native stderr as ErrorRecord objects.
    # Preserve that output without mistaking diagnostics for an exit-code failure.
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 1
        & $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() } | Tee-Object -FilePath $LogPath
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }
    if ($code -ne 0) { throw "$Name failed ($code). See $LogPath" }
}

Push-Location $repository
try {
    if ([IO.Path]::IsPathRooted($OutputDirectory)) {
        $output = [IO.Path]::GetFullPath($OutputDirectory)
    }
    else {
        $output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $hostDirectory = Join-Path $output 'host'
    $fixtureNames = @('smoke', 'input-memory', 'storage')
    $fixtureScripts = @($fixtureNames | ForEach-Object { Join-Path $repository "tests/native/$_.ps1" })
    $fixtureScripts += Join-Path $repository 'tests/native/Initialize-Msvc.ps1'
    foreach ($path in $fixtureScripts) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing fixture: $path" }
        if (Get-Item -LiteralPath $path -Stream Zone.Identifier -ErrorAction SilentlyContinue) {
            if (!$UnblockReviewedFixtures) {
                throw "Downloaded fixture is blocked: $path. Review the named scripts, then rerun with -UnblockReviewedFixtures. No execution policy is changed."
            }
            Unblock-File -LiteralPath $path
        }
    }
    if (!$NativeOnly) {
        $dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        Invoke-IrxStep 'Release build' $dotnet @('build', 'IraRationis.slnx', '-c', 'Release', '-p:Platform=Any CPU') (Join-Path $output 'build.log')
        Invoke-IrxStep 'Runtime contracts' $dotnet @('run', '--project', 'IraRationis.Runtime.Tests', '-c', 'Release', '--no-build', '-p:Platform=AnyCPU') (Join-Path $output 'contracts.log')
        Invoke-IrxStep 'Startup evidence contracts' $dotnet @('run', '--project', 'tests/startup-evidence/StartupEvidence.Contracts.csproj', '-c', 'Release', '--no-build', '-p:Platform=AnyCPU') (Join-Path $output 'startup-evidence.log')
        Invoke-IrxStep 'Versioned upload contracts' $dotnet @('run', '--project', 'tests/versioned-upload/VersionedUpload.Contracts.csproj', '-c', 'Release', '--no-build', '-p:Platform=AnyCPU') (Join-Path $output 'versioned-upload.log')
        Invoke-IrxStep 'Descriptor binding contracts' $dotnet @('run', '--project', 'tests/descriptor-bindings/DescriptorBindings.Contracts.csproj', '-c', 'Release', '--no-build', '-p:Platform=AnyCPU') (Join-Path $output 'descriptor-bindings.log')
        Invoke-IrxStep 'Storage payload contracts' $dotnet @('run', '--project', 'tests/storage-contract/StorageContract.csproj', '-c', 'Release', '--no-build', '-p:Platform=AnyCPU') (Join-Path $output 'storage-contracts.log')
        Invoke-IrxStep 'Runtime media contracts' $dotnet @('run', '--project', 'tests/runtime-media/RuntimeMedia.csproj', '-c', 'Release', '--no-build', '-p:Platform=AnyCPU') (Join-Path $output 'runtime-media.log')
        Invoke-IrxStep 'Native AOT publish' $dotnet @('publish', 'IraRationis.Cli', '-c', 'Release', '-r', 'win-x64', '-p:Platform=AnyCPU', '-o', $hostDirectory) (Join-Path $output 'publish.log')
    }
    $irx = (Resolve-Path -LiteralPath (Join-Path $hostDirectory 'irx.exe')).Path
    if (!$NativeOnly) {
        Invoke-IrxStep 'Graphics probe' $irx @('graphics-probe') (Join-Path $output 'graphics-probe.log')
    }
    # Each fixture normalizes the expected guest sentinel 42 to test exit code 0.
    # Run in a child PowerShell so fixture failures cannot terminate this runner.
    $shell = (Get-Process -Id $PID).Path
    foreach ($fixture in $fixtureNames) {
        $path = Join-Path $repository "tests/native/$fixture.ps1"
        Invoke-IrxStep "Native $fixture fixture" $shell @('-NoProfile', '-NonInteractive', '-File', $path, '-Irx', $irx) (Join-Path $output "$fixture.log")
    }
    Write-Host "Validation passed. Logs: $output"
}
finally {
    Pop-Location
}
