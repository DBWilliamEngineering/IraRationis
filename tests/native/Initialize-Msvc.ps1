# Shared x64 MSVC setup for native contract fixtures (Windows PowerShell 5.1 / PowerShell 7).
function Get-IrxMsvcTools {
    if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64' -or !$env:INCLUDE -or !$env:LIB) {
        return $null
    }

    $compiler = Get-Command cl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$compiler) { return $null }
    $directory = Split-Path -Parent $compiler.Source
    $librarian = Join-Path $directory 'lib.exe'
    $linker = Join-Path $directory 'link.exe'
    if (!(Test-Path -LiteralPath $librarian -PathType Leaf) -or !(Test-Path -LiteralPath $linker -PathType Leaf)) {
        return $null
    }
    return [pscustomobject]@{ Cl = $compiler.Source; Lib = $librarian; Link = $linker }
}

function Restore-IrxMsvcEnvironment {
    param([hashtable]$Previous)
    foreach ($name in $Previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $Previous[$name], 'Process')
    }
}

function Enter-IrxMsvcEnvironment {
    $previous = @{}
    $tools = Get-IrxMsvcTools
    if ($tools) {
        return [pscustomobject]@{ Tools = $tools; Previous = $previous }
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Visual Studio discovery is unavailable. Install Visual Studio or Build Tools with Desktop development with C++ and a Windows SDK.'
    }
    $installations = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0) { throw "vswhere failed ($LASTEXITCODE)." }
    if ($installations.Count -ne 1 -or [string]::IsNullOrWhiteSpace($installations[0])) {
        throw 'MSVC x64 tools were not found. Install Desktop development with C++ and a Windows SDK.'
    }
    $vcvars = Join-Path $installations[0] 'VC\Auxiliary\Build\vcvars64.bat'
    if (!(Test-Path -LiteralPath $vcvars -PathType Leaf)) { throw "Missing MSVC initialization script: $vcvars" }

    # cmd.exe owns the batch file and its environment. /u emits Unicode for SET.
    # Pass the path through the child environment, not through generated shell text.
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $env:ComSpec
    $start.Arguments = '/d /u /s /c "call "%IRX_MSVC_VCVARS%" >nul && set"'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.StandardOutputEncoding = [Text.Encoding]::Unicode
    $start.EnvironmentVariables['IRX_MSVC_VCVARS'] = $vcvars
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "MSVC environment initialization failed ($($process.ExitCode))." }
    }
    finally {
        $process.Dispose()
    }

    try {
        foreach ($line in ($output -split '\r?\n')) {
            # Skip CMD's drive-current-directory entries (=C:=...) and our private variable.
            $separator = $line.IndexOf('=')
            if ($separator -le 0) { continue }
            $name = $line.Substring(0, $separator)
            if ($name -eq 'IRX_MSVC_VCVARS') { continue }
            $value = $line.Substring($separator + 1)
            $original = [Environment]::GetEnvironmentVariable($name, 'Process')
            if ($original -cne $value) {
                $previous[$name] = $original
                [Environment]::SetEnvironmentVariable($name, $value, 'Process')
            }
        }
        $tools = Get-IrxMsvcTools
        if (!$tools) { throw 'vcvars64 completed but cl.exe, lib.exe, link.exe or the x64 SDK environment is unavailable.' }
        Write-Host "Initialized x64 MSVC: $($tools.Cl)"
        return [pscustomobject]@{ Tools = $tools; Previous = $previous }
    }
    catch {
        Restore-IrxMsvcEnvironment -Previous $previous
        throw
    }
}
