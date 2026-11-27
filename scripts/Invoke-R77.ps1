[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/validation/R77',
    [switch]$Validate,
    [switch]$UnblockReviewedFixtures,
    [switch]$DisableClzeroBatch,
    [switch]$DisableMovieScheduling
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = Split-Path -Parent $PSScriptRoot
$settings = @{
    IRX_GUARDED_MEMORY_PUBLICATION = '1'
    IRX_HOST_SHADER_DISK_CACHE = '1'
    IRX_ORDERED_TEXTURE_UPLOADS = '1'
    IRX_AUDIO_MIXER_SCHEDULING = '1'
    IRX_BINK_WORKER_SCHEDULING = $(if ($DisableMovieScheduling) { '0' } else { '1' })
    IRX_VEH_BATCH_CLZERO = $(if ($DisableClzeroBatch) { '0' } else { '1' })
    IRX_VEH_RESIDENT_PAGE_QUERY = '1'
    IRX_VEH_PROFILE = '1'
    IRX_TRACE_COLOR_FLOW = '0'
    IRX_R31_INPUT_COLOR_TRACE = '0'
    IRX_PREVIEW_COMPLETED_DRAW = '0'
    IRX_PREVIEW_WINDOW = '0'
    IRX_RUNTIME_LOCAL_SERVICES = '1'
    IRX_STARTUP_OBSERVATION = '1'
    IRX_AUDIO_TRACE = '1'
    IRX_RUNTIME_BRIDGE_TRACE = '0'
    IRX_ALLOW_INCOMPLETE_RENDERING = $null
    IRX_COMPUTE_INSTRUCTION_TRACE = '1'
}
$previous = @{}
Push-Location $repository
try {
    $output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory)) }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $settings['IRX_DRAW_CAPTURE_DIR'] = Join-Path $output 'draw-captures'
    $settings['IRX_COMPUTE_CAPTURE_DIR'] = Join-Path $output 'compute-captures'
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    if ($Validate) {
        & (Join-Path $PSScriptRoot 'Invoke-Validation.ps1') -OutputDirectory $output -UnblockReviewedFixtures:$UnblockReviewedFixtures
    }
    $irx = (Resolve-Path -LiteralPath (Join-Path $output 'host/irx.exe')).Path
    $log = Join-Path $output 'runtime.log'
    $ErrorActionPreference = 'Continue'
    & $irx run 2>&1 | ForEach-Object { $_.ToString() } | Tee-Object -FilePath $log
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) { throw "Guest terminated ($code). See $log" }
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
    Pop-Location
}
