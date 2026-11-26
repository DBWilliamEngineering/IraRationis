# R41 | Runtime launch audio acceptance

This is an original synthetic PE and external audio asset test for the normal `irx run` pipeline. It uses the same 20 ms, 48 kHz stereo Opus packet from R40's verified synthetic test. No commercial binaries, assets or account services are used.

## Apply

Requires the locally verified R40 patch (including its HLE project reference to Media, partial XApu declaration, PCM sink hook and WindowsPcmMonitor). Extract the R41 archive over that tree. No patch application scripts or project changes are needed. The published GitHub snapshot `44ad3c30` did not yet include R40, so retain and commit those R40 files too.

## Build the runtime and fixture

Run these commands in an x64 Visual Studio Developer PowerShell from the repository root. They build the synthetic fixture, not apply a patch.

```powershell
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native-r41
if ($LASTEXITCODE -ne 0) { throw 'Runtime publish failed' }

$irx = (Resolve-Path artifacts/native-r41/irx.exe).Path
$fixture = (Resolve-Path tests/native/runtime-audio).Path
$stage = (New-Item -ItemType Directory -Force artifacts/runtime-audio-r41).FullName
Copy-Item (Join-Path $fixture 'tone.opuspacket') $stage -Force

Push-Location $stage
try {
    & lib.exe /nologo /machine:x64 "/def:$fixture\xapu.def" /out:xapu.lib
    if ($LASTEXITCODE -ne 0) { throw 'Import library failed' }
    & cl.exe /nologo /W4 /WX /O2 /GS- /Zl "$fixture\probe.c" /link /NODEFAULTLIB /ENTRY:ProbeEntry /SUBSYSTEM:CONSOLE /BASE:0x600000000 /DYNAMICBASE /FIXED:NO /OUT:probe.exe xapu.lib kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compile failed' }
}
finally {
    Pop-Location
}

$probe = Join-Path $stage 'probe.exe'
```

## Run through the ordinary loader

```powershell
$env:IRX_RUNTIME_PCM_STREAM = '1'
try {
    & $irx run $probe
    if ($LASTEXITCODE -ne 42) { throw "Unexpected fixture exit: $LASTEXITCODE" }
}
finally {
    Remove-Item Env:IRX_RUNTIME_PCM_STREAM -ErrorAction SilentlyContinue
}
```

The fixture imports XApu through the HLE registry, reads `G:\tone.opuspacket` using the existing virtual filesystem mapping, activates voice 1, submits 50 packets, checks returned PCM buffer identity, frame count, finite nonzero PCM and guest result status, deactivates the voice and disconnects. The exit code 42 is the deliberate synthetic success sentinel. Missing asset returns 11; activation failure 12; decode/ABI failure 13; invalid PCM 14; deactivation/disconnect failures 15/16.

Expected host markers (counts are acceptance targets, not measurements from this patch):

```text
[audio] IRX-R41: diagnostic PCM observer enabled for stream=1; single voice, not a mixer.
[audio] IRX-R40-MONITOR: Windows PCM device opened; rate=48000; channels=2.
Title entry returned 42 (0x0000002A).
[audio] IRX-R40-MONITOR: accepted=50; completed=50; dropped=0.
[audio] IRX-R41: observed=50; frames=48000; accepted=50; completed=50; drained=True; faulted=False.
```

Listen for the synthetic tone. For negative control, unset the environment variable and rerun the same fixture; expect guest exit 42 with no observer/device markers. A bind-only launch must not open an audio device even if the variable is set. A wrong stream (e.g. `0`) should produce zero observed packets. The observer selects the first connection that produces valid PCM on the requested stream and does not switch connections; explicit per-connection registration has precedence. No global registration is active by default. This is a diagnostic single-voice tap, not a mixer. Queue drops, device errors and drain timeouts do not change the guest return code. Fail-fast termination cannot guarantee a final drain.

R41 does not implement GPU uploads or guest movie decoding. The Media Foundation decoder and R40 video-plane contracts remain untouched. This is not an assertion of commercial title compatibility. Test only executables and media you have authorization to run.

Windows build, loader and audio acceptance have not been performed in the authoring environment.
