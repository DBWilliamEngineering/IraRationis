# Runtime diagnostics

Use separate runs for functional investigation and performance measurement. Keep logs, traces, title-derived data, and matching build outputs under ignored artifact directories or outside the repository.

## Startup and input

Publish to `artifacts/native` using the [build instructions](../README.md#build-and-validate), then run from the repository root:

```powershell
$env:IRX_INPUT_TRACE = '1'
$env:IRX_STARTUP_OBSERVATION = '1'
$env:IRX_PROFILE_DETAIL_INTERVAL_MS = '10000'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force artifacts/logs | Out-Null

& .\artifacts\native\irx.exe run 2>&1 |
    Tee-Object -FilePath ".\artifacts\logs\startup-$stamp.log"
```

Reproduce one action, such as pressing and releasing Y at the Account Picker. Record when it happened and whether the screen changed. Inspect these signals together:

- Guest input reporting `Y=True`, followed by release. Host input alone does not prove that the title consumed the reading.
- A reading-callback registration reported as undelivered. [RegisterReading](../IraRationis.HLE/Input/GameInputModule/GameInputModule.cs) currently returns success without delivering those callbacks. This is a possible compatibility blocker, not a confirmed explanation of every stall.
- Missing imports, exceptions, startup phases, and network activity around the action.

The startup observer's `accountLifecycle=notObserved` field is a fixed observation label, not an authentication result. No account lifecycle implementation was found in the registered HLE modules during the source review; native dependencies may supply additional services. Do not infer a specific failing API from the label alone.

The authored media export and diagnostic PCM observation are separate from the title's own media ABI. See [Runtime Integration](RUNTIME-INTEGRATION.md).

## DNS and socket operations

In a separate elevated PowerShell terminal, from the repository root, list the running processes and replace the example PID with the game-hosting `irx` process:

```powershell
Get-Process irx | Select-Object Id,Path,StartTime
$gameProcessId = 12345
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$networkTrace = ".\artifacts\profiling\network-$stamp.etl"

& .\artifacts\native\irx.exe network-trace $gameProcessId $networkTrace 60
if ($LASTEXITCODE -ne 0) { throw 'Network capture failed or reported lost events; inspect its output and metadata.' }

pwsh -File .\scripts\Export-NetworkTrace.ps1 -TracePath $networkTrace `
    -ProcessId $gameProcessId -OutputPath "$networkTrace.jsonl"
```

Press Y during the capture. The controller defaults to 60 seconds and accepts 1-300 seconds. Ctrl+C stops that capture; it also stops when the observed process exits. Each capture uses a unique ETW session and a circular file capped at 64 MiB. Choose a new output path; existing recordings are rejected.

| File | Purpose |
| --- | --- |
| `.etl` | Original events for further decoding |
| `.etl.txt` | Process identity, session name, timestamps, scope, and lost-event counters |
| `.etl.jsonl` | Process-filtered decoded events, including socket addresses when available |

The selected providers are DNS Client, Winsock NameResolution, and Winsock Sockets. The event allowlist records operation metadata and excludes socket-option payload events. It does not enable packet capture, HTTP headers/bodies, or TLS-secret capture. DNS names and endpoints can still appear in the output.

Events from service or worker processes can fall outside the PID filter. Requests made before recording are absent, and circular recording can overwrite earlier events. Socket success is not proof of completed login; an empty export is not proof that no network work occurred. If recording starts at the Account Picker and shows no useful activity, a follow-up may need to begin earlier in startup.

The controller stops only its own session. If stopping fails, its error supplies a `logman stop <session-name> -ets` command for that exact session. Do not stop unrelated profiler sessions.

Implementation: [NetworkTraceCommand](../IraRationis.Cli/Diagnostics/Network/NetworkTraceCommand.cs), [NetworkTraceSession](../IraRationis.Cli/Diagnostics/Network/NetworkTraceSession.cs), and [exporter](../scripts/Export-NetworkTrace.ps1).

## CPU profiling and matching symbols

For a performance run, disable the extra startup/input and network capture used above. Keep rendering settings, scene, and capture length consistent. Preserve the EXE, native DLL, and PDB files from the exact build being measured. Local Native AOT publication includes `irx.pdb` and `irx-native.pdb`; packaged distributions currently omit PDBs.

```powershell
Remove-Item Env:IRX_INPUT_TRACE -ErrorAction SilentlyContinue
Remove-Item Env:IRX_STARTUP_OBSERVATION -ErrorAction SilentlyContinue
Remove-Item Env:IRX_PROFILE_DETAIL_INTERVAL_MS -ErrorAction SilentlyContinue
```

The successful historical CPU capture used a 5 ms sampling interval. A bounded 30-60 second capture of the same scene is a useful next comparison. Record profiler settings and check lost-event counters before interpreting results. Avoid overlapping CPU captures or changing sampling settings while another profiler owns a session.

`IRX_PROFILE_HOST_CPU=1` enables sampled host CPU attribution by graphics stage. `IRX_PROFILE_TIMING=0` disables stage timing, and `IRX_PROFILE_THREADS=0` disables periodic thread attribution. Keep these settings consistent between runs. Stage timing is inclusive; CPU time, elapsed time, GPU execution, and presentation rate measure different things.

The optimized native bridge emits a full PDB. Symbol generation does not recreate private symbols for an older binary. A nearby filename or release label is not sufficient: image identity and PDB identity must match.

### Historical trace interpretation

The supplied R71 trace recorded approximately 128.9 seconds at a 5 ms interval without lost events/buffers. Its managed Native AOT image matched the available EXE/PDB. Roughly 60% of sampled process stacks included virtual-memory queries/exception dispatch, and roughly 17% included physical-view unmapping. These inclusive shares overlap and are not predicted FPS improvements.

The supplied R25/R66 recordings lost approximately 5.57/5.56 million events. The relevant R66 process was present for only about 441 ms, and its traced image did not match the available local R66 PDB. Those recordings are not reliable numeric before/after baselines.

The [mapped-view index](../IraRationis.HLE/Memory/MappedViewIndex.cs) targets the observed bookkeeping cost: overlap lookup is O(log n + k) for n views and k overlaps; alias resolution works by contiguous view portions. Windows mapping calls remain serialized under the owner lock. A fresh trace is needed to quantify the change and the native exception-index work.

## Reporting a result

Provide the build commit, command line, environment switches, scene/action, log, and capture metadata. For network investigation, include the JSONL export and keep the raw ETL available. For CPU analysis, retain exact matching binaries and symbols. Report a successful build, component test, authored fixture, integrated title behavior, and performance measurement as separate observations.

Historical `IRX-R...` markers remain in logs for compatibility. They identify diagnostic families; current code and documentation are organized by responsibility rather than release number.
