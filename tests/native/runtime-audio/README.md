# Native runtime audio acceptance

This original synthetic PE and external Opus packet exercise ordinary `irx run` loading, the virtual filesystem, the XApu ABI, and an optional diagnostic PCM observer. No commercial binary, asset, network, or account service is involved.

## Build

From the repository root with the SDK and MSVC x64 tools installed:

```powershell
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native
dotnet msbuild tests/native/runtime-audio/RuntimeAudio.proj -t:Build

$irx = (Resolve-Path artifacts/native/irx.exe).Path
$probe = (Resolve-Path artifacts/runtime-audio-r41/probe.exe).Path
```

Check each exit status before continuing. The fixture project discovers Visual Studio through `VSINSTALLDIR` or `vswhere`, selects the x64 toolchain, and copies `tone.opuspacket` beside the fixture. Its current default output remains `artifacts/runtime-audio-r41`. Override toolchain discovery with `-p:VisualStudioRoot="<Visual Studio installation>"` if needed.

## Headless and audible checks

With the observer unset, expect the deliberate success sentinel `42` without opening the PCM monitor:

```powershell
Remove-Item Env:IRX_RUNTIME_PCM_STREAM -ErrorAction SilentlyContinue
& $irx run $probe
if ($LASTEXITCODE -ne 42) { throw "Unexpected fixture exit: $LASTEXITCODE" }
```

For audible acceptance, select voice 1 and listen to the synthetic tone:

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

The fixture reads `G:\tone.opuspacket`, activates voice 1, submits 50 packets, checks PCM buffer identity, frame count, finite nonzero samples, and result status, then deactivates and disconnects. Failure sentinels distinguish a missing asset (11), activation failure (12), decoding/ABI failure (13), invalid PCM (14), and deactivation/disconnection failures (15/16).

Expected audible-run evidence uses existing log markers:

```text
[audio] IRX-R41: diagnostic PCM observer enabled for stream=1; single voice, not a mixer.
[audio] IRX-R40-MONITOR: Windows PCM device opened; rate=48000; channels=2.
Title entry returned 42 (0x0000002A).
[audio] IRX-R40-MONITOR: accepted=50; completed=50; dropped=0.
[audio] IRX-R41: observed=50; frames=48000; accepted=50; completed=50; drained=True; faulted=False.
```

These are acceptance targets, not a fresh measurement. Bind-only launch must not open an audio device. Selecting another stream should observe zero packets. The observer attaches to the first connection producing valid PCM for the selected stream and does not switch connections; explicit per-connection registration takes precedence.

This is a single-voice diagnostic tap, not the game's mixer. Queue drops, device faults, or drain timeouts do not change the guest return code, so inspect host counters too. CI builds and invokes the headless fixture; speaker output and game audio integration are separate acceptance work.
