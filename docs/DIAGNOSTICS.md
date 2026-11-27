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

The startup observer's `accountLifecycle=notObserved` field is a fixed observation label, not an authentication result. It predates the opt-in local-user service and does not track that service's state. Use actual query and local-service operation records; do not infer a specific failing API from the label alone.

The authored media export and diagnostic PCM observation are separate from the title's own media ABI. See [Runtime Integration](RUNTIME-INTEGRATION.md).

### Input correlation

With `IRX_INPUT_TRACE=1`, current builds emit bounded `[input] event=...` records carrying UTC, process/native-thread identifiers, and monotonic ticks/frequency. `HidYPressed` / `HidYReleased` describe physical observations; `GuestYPressed` / `GuestYReleased` describe readings consumed by the title. Each category records its first eight observations and later powers of two, with cumulative suppression counts. Frequent polling therefore cannot consume the initial Y-event budget. Historical `IRX-R30-*` records below predate this format.

`DeviceRegistration`, `DeviceCallbackEnter` / `DeviceCallbackReturn`, and `FindDeviceSuccess` / `FindDeviceFailure` expose the existing controller-discovery path. Tokens, device pointers, slots, callback addresses, and statuses support correlation. These diagnostics preserve registration, delivery, and lookup behavior; they do not implement account association or the missing reading callbacks.

### Instruction-emulation counters

For a targeted diagnostic run, also set these switches before launching the same executable:

```powershell
$env:IRX_VEH_PROFILE = '1'
$env:IRX_VEH_BATCH_CLZERO = '0'
```

The native handler reports the first successful emulation of each instruction kind per thread, then powers of two from 1,024 onward. Its records include `kind`, `handled`, `zeroedLines`, `regionQueries`, and image-relative `rva`. Counters are cumulative and sampled; do not sum every record as independent work. `regionQueries` counts both instruction-read and destination-write validation.

At the Account Picker, hold Y for ten seconds, release it, and leave the game running for another ten seconds. Record whether the screen responds and retain the guest input records around the hold. This distinguishes a short press missed between slow current-state reads from a press delivered to the guest without completing the account workflow.

Keep the experimental `IRX_VEH_BATCH_CLZERO` switch off for this diagnostic baseline. Its existing opt-in path batches only a narrowly recognized loop on ordinary private read/write memory; it does not cover every CLZERO instruction or mapping. Opcode counters should establish which path the title uses before a separate controlled comparison.

## Runtime service bridge

The supplied code export identifies dynamic loading of `xgameruntime.dll`, followed by runtime initialization and provider/interface queries. Static import totals cannot establish these later operations succeed. The opt-in C# [runtime observer](../IraRationis.HLE/Runtime/Diagnostics/RuntimeBridgeDiagnostics.cs) decorates successful host resolutions in `HleResolver`; it does not claim additional HLE modules or change dependency selection. Disabled observation returns the existing host addresses directly.

Build and run the [authored forwarding contracts](RELEASING.md#runtime-bridge-diagnostic-validation) first. Then use the new publication from the repository root:

```powershell
$irx = (Resolve-Path .\artifacts\runtime-bridge\irx.exe).Path
$env:IRX_RUNTIME_BRIDGE_TRACE = '1'
$env:IRX_INPUT_TRACE = '1'
$env:IRX_STARTUP_OBSERVATION = '1'
$env:IRX_VEH_RESIDENT_PAGE_QUERY = '1'
$env:IRX_VEH_BATCH_CLZERO = '0'
$env:IRX_VEH_PROFILE = '1'
$env:IRX_PROFILE_DETAIL_INTERVAL_MS = '10000'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force artifacts/logs | Out-Null

& $irx run 2>&1 |
    Tee-Object -FilePath ".\artifacts\logs\runtime-bridge-$stamp.log"
```

Let the Account Picker settle for ten seconds. Hold Y for about two seconds, release, and wait ten seconds. Repeat once, then leave the title running for twenty seconds before closing it. Record whether the icon responds, another screen appears, or the process stops. Retain the complete log, including startup; the bridge may initialize long before the first press. This is a diagnostic run, not a performance benchmark. An additional identical network capture is not needed for this first comparison.

| Record | What it establishes |
| --- | --- |
| `phase=note`, `enabled=True` | Observer configuration. Separate binding notes identify intercepted host functions. |
| `privateAbiVerified=True` | The startup System32 DLL fingerprint matches the inspected build; each resolved private export still needs its path and RVA check. |
| Host/title context | Executable locations, adjacent `MicrosoftGame.config` presence, and package-identity query status. Package names and manifest contents are not read. |
| `operation=LibraryLoad` begin/end | A recognized runtime load request, flags, returned module, actual module path, and preserved last-error snapshot. |
| `operation=ExportLookup` begin/end | Runtime export or ordinal requested, resolved/returned addresses, and whether a private observer was installed. |
| `operation=Initialize`, `InitializeEx`, or `InitializeEx2` begin/end | Full-width original arguments and exact returned HRESULT. The options pointer remains opaque. |
| `operation=QueryApi` begin/end | Readable provider/interface GUIDs and exact HRESULT; successful output is recorded as a raw object pointer. Returned interfaces are not wrapped. |
| `phase=limit` / `phase=summary` | Current QueryApi sampling is per provider/interface: first eight calls and powers of two, at most 64 keys, plus 32 otherwise unsampled failure pairs. Other operations keep their first-128-call limit. A disposal summary is a snapshot; process termination can omit it. |

Pair begin/end records by operation and ID, and correlate with UTC/native thread or monotonic timestamps. An unmatched begin can identify a call that did not return within the recording, but abrupt termination and logging failure must be considered. IDs are unique within a session, not globally chronological across operation kinds. `caller` is the first registered guest-image return address found in a bounded 32-frame unwind; `unavailable` means no such frame was recovered. It is not an instruction trace. `win32Error` preserves the thread's last-error state; successful calls and HRESULT-returning functions may leave a stale value, so interpret their returned result first.

The private query/initialization calling shapes were independently checked against guest machine code and host `xgameruntime.dll` version `10.0.26100.9656`. [ABI policy](../IraRationis.HLE/Runtime/Diagnostics/RuntimeBridgeAbiPolicy.cs) requires SHA-256 `38D212D8FC4F2723E2D0CAD88A0A78144D0E0CED9BCCE4A83468E7E90C6E88DF`, the captured System32 path, and the inspected export RVA. The file is hashed once before guest execution. This is a compatibility snapshot, not an atomic guarantee against replacing the file during execution. Unknown builds/paths/addresses retain their original export pointers and report `privateAbi=unverified`; loader observations remain available. `XErrorReport`, uninitialization, and all other runtime exports remain unwrapped.

Callbacks preserve arguments, results, output memory, and both native/managed error channels. Native targets are bound once; an address conflict is returned unchanged instead of retargeting a cached callback. No additional loader reference pins the module. Closing the diagnostic session stops new interception; already returned callbacks continue forwarding to their original targets. Diagnostic readers use bounded, freshly validated readable pages and reject known guard/no-access/uncommitted pages before copying. Protection changes between query and copy remain possible. ASCII export/ANSI-path recognition is bounded; Unicode loader names use the UTF-16 reader. Logging exceptions are contained, and repeated writer failures stop further output.

The package probe uses a zero-length [GetCurrentPackageFullName](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname) query. Its status distinguishes no package identity from a required name-buffer size; this alone is not an account-initialization result or a reason to copy manifests into the host directory.

The [Victory runtime reference](https://github.com/avatarmanz/VIctory-7900XTX/blob/126acb35b509bdbe097d607f198d7a2736de5202/crates/victory-hle/src/modules/xgameruntime.rs) and [Five Star user completion reference](https://github.com/ThatRecompDev/five-star-recomp/blob/9864ac5ee1cf9ab7909b1be427430d87f1c3eb26/src/FiveStar.Loader/Shims/Gdk/UserAddOperation.cs) supply offline runtime/user behavior and asynchronous completion. They help identify required contracts; their gameplay/performance claims were not independently tested, and their implementation was not copied into this observer. The separate [local service implementation](runtime-services.md) now provides explicit C# user, title-ID, and storage behavior, with its own pending validation.

### Supplied bridge results

`runtime-bridge-20261126-155547.log` records `InitializeEx2` returning success at physical lines 151-154. The storage provider/interface pair `f4faf4d4-2d04-4fce-b3e0-474a713a3e84` / `41a4e10c-5a7e-41d9-8c37-37bde62a07d6` returns `E_FAIL` at lines 227-231. Of the first 128 observed query completions, 127 succeed and one fails. The old global query budget expires at lines 1454-1455, approximately eleven seconds before the first guest Y press at line 6445. Seven complete guest Y press/release pairs and two successful device-registration callback deliveries are visible. Account Picker remained blocked; none of these observations proves its cause.

The initial `NativeCommandError` text is Windows PowerShell 5's stderr formatting. It is not itself a game exception. Wrapped physical lines must be reassembled before counting records. There is no final bridge summary in this recording. The new per-key sampler prevents repeated queries to one interface from exhausting another interface's initial samples. Promoted failures carry `deferredForFailure=True`, original start ticks, and `callerCapture=afterResult`; their caller unwind was captured after the returned result. Suppression and aggregate totals remain visible within the documented budgets.

### Local runtime service capture

The new services are opt-in. Damon reported all tests passed on November 26, 2026; actual title integration remains pending. Publish the current CLI to `artifacts/local-services` using the [build and contract sequence](RELEASING.md#runtime-bridge-diagnostic-validation), then capture the game. The outstanding host-interface review is recorded in the [service handoff](runtime-services.md). Start PowerShell 7 with `pwsh -NoProfile`, then run these commands from the repository root. Direct redirection in PowerShell 7 avoids the Windows PowerShell 5 error-record wrapping seen in the supplied log.

```powershell
$irx = (Resolve-Path .\artifacts\local-services\irx.exe).Path
$PSNativeCommandUseErrorActionPreference = $false
$env:IRX_RUNTIME_LOCAL_SERVICES = '1'
$env:IRX_RUNTIME_BRIDGE_TRACE = '1'
$env:IRX_INPUT_TRACE = '1'
$env:IRX_STARTUP_OBSERVATION = '1'
$env:IRX_VEH_RESIDENT_PAGE_QUERY = '1'
$env:IRX_VEH_BATCH_CLZERO = '0'
$env:IRX_VEH_PROFILE = '1'
$env:IRX_PROFILE_DETAIL_INTERVAL_MS = '10000'
New-Item -ItemType Directory -Force artifacts/logs | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = ".\artifacts\logs\local-services-$stamp.log"
& $irx run > $log 2>&1
$runExitCode = $LASTEXITCODE
Write-Host "Exit code: $runExitCode; log: $log"
```

Let the picker settle for ten seconds, hold Y for two seconds, release, and wait ten seconds. If it stays there, repeat once and leave another twenty seconds before closing. If it advances, continue toward the menu and record the next blocker. Report the actual visible screen, audio, and whether the process exited normally. Inspect `Enabled`, `UserAddBegin`, `UserAddCompleted`, `UserAddResult`, storage-query/path records, and explicit unsupported-operation records together. Add entry is logged after argument validation but before host Begin; completion is logged before calling host Complete. Neither record proves callback delivery. Use the [handoff decision table](development-handoff.md#reading-the-next-game-log) before interpreting missing records or differing async-block addresses.

The local route retains the real runtime module and uses the real host async queue; online identity, tokens, and privileges remain unsupported. The exact host provider/interface policy is intentionally limited and still under method-ownership review. For a same-binary host baseline, set `IRX_RUNTIME_LOCAL_SERVICES='0'` and choose another log filename. Keep the remaining switches unchanged. This run tests functionality; it is not a controlled FPS benchmark. The existing independent media/video commands remain useful separate checks.

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

The selected providers are DNS Client, Winsock NameResolution, and Winsock Sockets. The event allowlist records DNS and socket lifecycle metadata and excludes socket-option payload events. The selected Winsock Sockets provider has no send/receive events: transfers over an existing connection can therefore occur without appearing in this capture. It does not enable packet capture, HTTP headers/bodies, or TLS-secret capture. DNS names and endpoints can still appear in the output.

Events from service or worker processes can fall outside the PID filter. Requests made before recording are absent, and circular recording can overwrite earlier events. Socket success is not proof of completed login; an empty export is not proof that no network work occurred. If recording starts at the Account Picker and shows no useful activity, a follow-up may need to begin earlier in startup.

The controller stops only its own session. If stopping fails, its error supplies a `logman stop <session-name> -ets` command for that exact session. Do not stop unrelated profiler sessions.

Implementation: [NetworkTraceCommand](../IraRationis.Cli/Diagnostics/Network/NetworkTraceCommand.cs), [NetworkTraceSession](../IraRationis.Cli/Diagnostics/Network/NetworkTraceSession.cs), and [exporter](../scripts/Export-NetworkTrace.ps1).

### Account Picker network observation

The supplied `account-network-20261126-143829.etl` targets process 49032. Its recorded window is November 26, 2026, 20:38:29.983-20:39:12.975 UTC (14:38:29.983-14:39:12.975 America/Chicago): 42.992 seconds, beginning approximately 83 seconds after process creation. The session requested 60 seconds but ended earlier; the metadata does not distinguish process exit from cancellation. Both session metadata and ETL statistics report zero lost events and buffers.

Raw ETL decoding contains session metadata and no events from the three selected network providers. The installed provider manifests confirm the selected GUIDs, event IDs, levels, and keywords. This establishes no observed DNS/socket lifecycle operations in the capture, not that the title performed no network work or that an account service rejected a request. Earlier connections, existing-connection transfers, and service-owned activity remain outside this evidence. A known-operation positive-control capture has not yet established the recorder's end-to-end behavior.

The accompanying `resident-page-20261126-143706.log` contains five complete guest-consumed Y press/release pairs during the approximate capture window. Their physical line pairs are 6858/6863, 6886/6890, 6892/7048, 7050/7129, and 7133/7265; several include sustained holds. Input records have no UTC timestamps, and the startup timer begins inside the run command rather than at process creation, so correlation is approximate. The final startup snapshot reports 1,928 successful presents from 1,928 attempts at 124.014 seconds. The later final summary reports 1,937 presents and 16.48/s. Resident-page validation remains enabled, batching remains disabled, and the latest sampled checkpoint again records 2,097,152 resident queries/hits with zero destination region fallbacks.

Static inspection of the title and `libHttpClient.GDK.dll` imports identifies WinHTTP request APIs, and the title imports `CoCreateInstance` and `GameInput.dll` ordinal 1. The current HLE maps that ordinal to the same factory as `GameInputCreate`. Imports establish available boundaries, not calls made during Y. Source review identifies unobserved GameInput interface-query failures, device-lookup failures, and notification-registration no-ops as useful next instrumentation points. Record actual calls, identifiers, return codes, and timestamps before assigning the picker stall to any one of them. The empty network trace does not establish a failing account API.

The subsequent `account-network-20261126-145048.etl` targets process 52516 and begins approximately 35.389 seconds after process creation. Its ETL window spans 48.508 seconds, again with zero lost events/buffers and no selected network-provider events. The accompanying `resident-page-20261126-145012.log` records four complete guest Y pairs at physical lines 6196/6198, 6226/6333, 6335/6412, and 6463/6594 during the approximate window. Its last startup snapshot reports 1,223 successful presents from 1,223 attempts at 78.005 seconds; the later summary reaches 1,225 presents at 17.28/s. This earlier-starting capture repeats the negative observation but retains the same provider, timing, and existing-connection limits. Further investigation should validate recorder behavior with a known operation and follow the account action's actual call/result path.

## CPU profiling and matching symbols

For a performance run, disable the extra runtime-bridge/startup/input observations and network capture used above. Keep rendering settings, scene, and capture length consistent. Preserve the EXE, native DLL, and PDB files from the exact build being measured. Local Native AOT publication includes `irx.pdb` and `irx-native.pdb`; packaged distributions currently omit PDBs.

```powershell
Remove-Item Env:IRX_RUNTIME_BRIDGE_TRACE -ErrorAction SilentlyContinue
Remove-Item Env:IRX_INPUT_TRACE -ErrorAction SilentlyContinue
Remove-Item Env:IRX_STARTUP_OBSERVATION -ErrorAction SilentlyContinue
Remove-Item Env:IRX_PROFILE_DETAIL_INTERVAL_MS -ErrorAction SilentlyContinue
Remove-Item Env:IRX_VEH_PROFILE -ErrorAction SilentlyContinue
```

The successful historical CPU capture used a 5 ms sampling interval. A bounded 30-60 second capture of the same scene is a useful next comparison. Record profiler settings and check lost-event counters before interpreting results. Avoid overlapping CPU captures or changing sampling settings while another profiler owns a session.

`IRX_PROFILE_HOST_CPU=1` enables sampled host CPU attribution by graphics stage. `IRX_PROFILE_TIMING=0` disables stage timing, and `IRX_PROFILE_THREADS=0` disables periodic thread attribution. Keep these settings consistent between runs. Stage timing is inclusive; CPU time, elapsed time, GPU execution, and presentation rate measure different things.

The optimized native bridge emits a full PDB. Symbol generation does not recreate private symbols for an older binary. A nearby filename or release label is not sufficient: image identity and PDB identity must match.

### Historical trace interpretation

The supplied R71 trace recorded approximately 128.9 seconds at a 5 ms interval without lost events/buffers. Its managed Native AOT image matched the available EXE/PDB. Roughly 60% of sampled process stacks included virtual-memory queries/exception dispatch, and roughly 17% included physical-view unmapping. These inclusive shares overlap and are not predicted FPS improvements.

The supplied R25/R66 recordings lost approximately 5.57/5.56 million events. The relevant R66 process was present for only about 441 ms, and its traced image did not match the available local R66 PDB. Those recordings are not reliable numeric before/after baselines.

The [mapped-view index](../IraRationis.HLE/Memory/MappedViewIndex.cs) targets the observed bookkeeping cost: overlap lookup is O(log n + k) for n views and k overlaps; alias resolution works by contiguous view portions. Windows mapping calls remain serialized under the owner lock. Comparisons need equivalent scenes and capture intervals before attributing a speedup to this change.

### Account Picker capture: November 26, 2026

The supplied `R73-CPU.etl` spans 70.666 seconds at a 5 ms sampling interval, with zero reported lost events or buffers. The observed `irx` process, PID 40220, was already running when recording began and exited approximately 61.995 seconds into the trace. Both `irx.exe` and `irx-native.dll` matched their local PDBs; the matching Windows kernel symbols were also resolved. Derived reports are local artifacts under `artifacts/profiling/r73-analysis`.

| Observation | Evidence and interpretation |
| --- | --- |
| Serial exception-path cost | In the resolved stack report, 12,006 of 12,211 sampled stacks on thread 5744 include `VirtualQuery`: approximately 98.3%. Across process stacks, this is 12,006 of 14,489, approximately 82.9%. The neighboring `Handle`, `Emulate`, and `RegionEnd` frames identify the native validation path as dominant. Inclusive shares overlap. |
| CLZERO destination validation | Raw stacks contain 11,454 returns to `Emulate` RVA `0x1FAC`, the call checking the CLZERO destination, and 555 to `0x1E4E`, the instruction-read check. The exact native binary's disassembly confirms both call sites. Destination checks account for approximately 95.4% of these identified caller samples; these are sampled frames, not invocation counts. |
| Kernel leaf cost | `MiGetPageProtection` and `MiQueryAddressState` account for approximately 66.9% and 13.7% of the process's weighted CPU samples. The sample weight totals 73.276 CPU seconds across threads; it is not elapsed time or FPS. |
| Slow presentation | The paired `R73-20261126-133235.log` reports 29 successful presents after 112.009 seconds. Late presentation gaps are roughly 3-4 seconds. Its final interval reports 4.6 ms total fence wait and 0.325 ms average present time, directing investigation to work between presents. |
| Y delivery unproven | The log contains 39 HID `Y=True` observations, but no guest `Y=True` record. These are observations, not 39 distinct presses. Guest current-state reading is active; missing a short press between slow reads is a hypothesis to test with a sustained hold. |
| Callback and network limits | No reading-callback registration appears in this run. The existing callback-delivery gap therefore is not established as its cause. No network capture or endpoint result was supplied, so the log does not establish connectivity or an authentication failure. |

The previous unmapping/LINQ chain is no longer a material named hotspot in this capture. This is encouraging but does not quantify an optimization: the recordings cover different intervals, and the new trace begins after startup. The exception-query path remains dominant. Exact instruction counters were absent, and the log reports CLZERO batching disabled; the capture cannot split ordinary CLZERO from the recognized-loop variant.

The next performance work should target repeated destination validation while preserving guard-page, protection, alias, and mapping-lifetime semantics. C# should own mapping policy and any explicitly maintained validity metadata; the native VEH boundary can consume that metadata with a validated fallback. The current image-membership index does not describe destination permissions, and direct host memory APIs can bypass managed bookkeeping. An unchecked permanent region cache would therefore be premature. Additional rendering workers or a larger VRAM budget do not remove this serial query cost.

### Instruction counters and sustained input follow-up

The subsequent `instruction-input-20261126-135213.log` confirms the recognized `CLZERO_LOOP` variant. Its nine instruction-counter records all belong to thread 48320 (`0xBCC0`); sampled instruction locations are image-relative addresses `0x4E8AA70`, `0x4E8AA90`, and `0x4E8AAB0`. At the last counter report, `handled=0x20000`, `zeroedLines=0x20000`, and `regionQueries=0x40000`: at least 131,072 single-line emulations, 8 MiB of cumulative clearing, and 262,144 validation queries. These are cumulative sampled checkpoints, not final totals or unique memory volume. Batching was disabled.

The sustained Y press reached the guest at reading 80 (`Y=True`, physical log line 8278), followed by release at reading 100 (`Y=False`, line 8864). Startup snapshots bracket these guest transitions at approximately 232-234 and 278-280 seconds. Damon reports that the Y icon animates when pressed, while the screen stays on Account Picker. Together, the log and visible response establish input delivery and UI recognition for this flow. The next account investigation should follow the action after the press; missed short presses cannot fully explain the stall. This evidence does not identify a failing account API or prove that faster presentation would resolve it. No reading callback was registered. During the hold, reported presentation gaps average approximately 2.21 seconds; the final startup snapshot reports 109 successful presents after 298.015 seconds. The busy thread still consumes approximately one CPU core.

The next controlled comparison can use the same archived executable with the existing opt-in batch path:

```powershell
$env:IRX_VEH_PROFILE = '1'
$env:IRX_VEH_BATCH_CLZERO = '1'
```

Launch a fresh process using the startup/input procedure above and reproduce the same scene and sustained press. The switch is read when the handler is installed. Keep the binary, other settings, and observation interval consistent, and retain a separate log. Restore `IRX_VEH_BATCH_CLZERO=0` afterward unless intentionally running another batch comparison.

The enabled banner establishes configuration, not eligibility or a speedup. Batching additionally requires ordinary private read/write memory and the exact supported loop; mapped or specially protected destinations remain single-line operations. Each batch is bounded by the queried region and a 1,024-line limit, then resumes the original loop tail. Mapping validation does not synchronize concurrent remapping, so this remains an experimental path.

`handled` is per thread and instruction kind, while `zeroedLines` and `regionQueries` span instruction kinds on that thread. For that older binary, a single snapshot with `zeroedLines > floor(regionQueries / 2)` demonstrates multi-line batching, because each single-line clear requires two region queries. Do not apply that formula to the new resident-page path: it replaces some region queries with page queries. New builds report `batchRuns` and `batchedLines` directly. Compare presentation intervals and title behavior separately from batching counters; a later equivalent-scene ETL can quantify the remaining validation cost.

### Fresh resident-page validation

The new destination-only path is opt-in through C# [instruction-emulation options](../IraRationis.Loader/Instructions/InstructionEmulationOptions.cs). It queries the specific page containing the aligned 64-byte destination. Only valid, non-bad working-set metadata with exactly `PAGE_READWRITE` authorizes that one cache-line store. Every other result uses the original region validator. No permission is cached between instructions, and instruction-byte decoding keeps its original region-boundary behavior.

Microsoft documents [page-specific working-set queries](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-queryworkingsetex) separately from [VirtualQuery's region scan](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualquery). Avoiding repeated region discovery is the performance hypothesis; a fresh query still does not synchronize concurrent protection or mapping changes. The native exception boundary retains SEH and preserves the thread's last-error value.

The query API is callable from C#. Its placement here follows the existing native exception boundary: [VEH callbacks have allocation and synchronization restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winnt/nc-winnt-pvectored_exception_handler), and managed exception handling is not a substitute for the existing native SEH containment around a faulting memory access. Configuration, hardware discovery, probe orchestration, and authored allocation tests remain C#. The native probe export exercises the production validator without introducing a managed callback inside VEH.

After building and publishing the new executable/DLL together, run `irx instruction-probe` before a title comparison. For the first title run:

```powershell
$env:IRX_VEH_PROFILE = '1'
$env:IRX_VEH_BATCH_CLZERO = '0'
$env:IRX_VEH_RESIDENT_PAGE_QUERY = '1'
```

Use `IRX_VEH_RESIDENT_PAGE_QUERY=0` with the same executable for the baseline. Keep batching off to isolate the validator change. An eligible batch request still uses the full region/type query; working-set attributes never establish private-memory batching eligibility. All switches are read before handler publication. The path remains opt-in; the initial contract and title results below do not establish broad compatibility.

| Counter | Meaning |
| --- | --- |
| `instructionQueries` | Region queries validating instruction bytes |
| `destinationQueries` | Region queries validating clear destinations, including fallbacks |
| `regionQueries` | Sum of the two region-query counters; retained for log compatibility |
| `residentQueries` / `residentHits` | Fresh page-query attempts and accepted single-line results |
| `batchRuns` / `batchedLines` | Successful multi-line operations and lines cleared by those operations |

Total memory-query calls are `regionQueries + residentQueries`. A smaller region-query count alone is not a lower total query count or a measured speedup. Nonresident pages can require both a page attempt and a region fallback. Compare timings and presentation at equivalent scenes, then capture a matching-symbol ETL if behavior is correct.

### Resident-page validation results

On November 26, 2026, Damon supplied passing `veh-probe` and `instruction-probe` output, followed by `resident-page-20261126-142539.log`. The memory probe exercised 14 cases under each policy. Resident private pages and mapped aliases used the page-query path; demand-zero and copy-on-write cases in this run used the region fallback. Read-only, no-access, guard, reserved, and decommitted destinations were rejected. Alignment, address overflow, the last cache line before an inaccessible page, and recommit behavior also passed. Every reported case had `accessFaulted=0`. These are authored-allocation results, not proof of safety under every concurrent mapping change. The resident-private positive fixture pins its owned test page; production validation does not pin pages.

| Observation | Evidence and interpretation |
| --- | --- |
| Late presentation | The last ten summary intervals report 641 attempts over 38.00 seconds: approximately 16.87/s, with individual rates of 16.48-17.21/s. The previous instruction/input run's last ten intervals report 14 attempts over 31.99 seconds: approximately 0.44/s, with individual rates of 0-0.50/s. The roughly 38.5-fold difference describes these observed windows; different run lengths, builds, and input timing prevent treating it as a controlled benchmark. |
| Destination-query replacement | At physical log lines 6864-6866, thread 53812 (`0xD234`) reports 2,097,152 `CLZERO_LOOP` emulations and cleared lines, 2,097,152 resident queries/hits, and zero destination region queries. This represents 128 MiB of cumulative clearing, not unique memory. Instruction region queries remain at 2,098,044 across instruction kinds. These are cumulative checkpoints, not final process totals. |
| Batching excluded | The banner and counters report batching disabled, `batchRuns=0`, and `batchedLines=0`. The improvement occurred with single-line emulation; it does not validate the separate loop-batching path. |
| Presentation progress | The last startup snapshot at 104.001 seconds reports 1,275 successful presents from 1,275 attempts, 13,155 draws, and 1,246 dispatches. A later summary reaches 1,285 presents. The older run reached 109 successful presents at 298.015 seconds; these unequal durations are progress observations, not a rate comparison. |
| Account Picker still blocked | Repeated Y transitions reached the guest, and Damon confirms the faster build still stays on Account Picker. Input delivery and faster presentation have not completed the account workflow. No reading-callback registration or network capture identifies its cause. |

The counters establish that the new path replaced expensive destination region queries throughout the reported checkpoints. They do not establish where CPU time is now spent: the earlier ETL belongs to a different binary and path. Preserve the `artifacts/native-validation` executable, native DLL, and PDBs for a new 30-60 second CPU capture of the settled Account Picker, with resident queries enabled and batching disabled. Capture DNS/socket operations around Y in a separate run using the procedure above and the same published executable. Correlate connection outcomes with input; socket success alone cannot prove account completion, and no captured traffic cannot rule out earlier or service-owned activity.

### Host capability snapshots

Startup reports the executable's runtime-usable SIMD instruction sets, effective processor count, physical RAM total/availability, and the separate GC memory budget. C# runtime support checks reflect usable features; CPUID bits alone would not establish operating-system or published-code support. Unknown physical-memory results stay unavailable rather than being replaced with a GC limit.

The resident-page run reports SSE2 through SSE4.2, with no AVX entries. Its Native AOT compiler response file has no explicit instruction-set override. This reports the published executable's usable feature set, not a complete inventory of the physical processor; it does not establish that the host lacks AVX2. Evaluate any broader compilation target separately, preserving runtime guards and documenting the resulting minimum hardware requirement.

The graphics initialization captures the actual selected host adapter's memory and capability information. Host VRAM and the DXGI local-memory budget are separate from the synthetic memory description exposed to the guest. A startup budget is a snapshot, not a reservation or a lifetime guarantee.

These observations provide inputs for later bounded scheduling and residency policies. They do not enable a MIP streaming subsystem, automatically add shader workers, or raise the existing resource caps. SIMD specialization should follow measured CPU workloads, retain a scalar/reference path, and verify the emitted Native AOT code. Wider vectors reduce constant work per element; they do not generally change an algorithm's asymptotic complexity.

## Title capture and comparison

Use PowerShell 7 from the repository root. The first command builds and runs the validation suite, publishes the host, and launches the title only after validation succeeds.

```powershell
$run = 'artifacts/validation/title-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
.\scripts\Invoke-R77.ps1 -OutputDirectory $run -Validate -TraceMovieAudio
```

Select the same profile and settings. Record the intro's wall time, when audio drops, whether the settings screen advances, and whether menu navigation responds. Keep the full `runtime.log` and any new `draw-captures` or `compute-captures` directories. Preserve the matching binary and symbols in `host`.

The launcher saves the host executable hash, selected options, and inherited measurement switches in `invocation.json`. It defaults to a 10-second interval for detailed thread and resource queries. Set `-ProfileDetailMilliseconds 0` to omit those detail queries while retaining regular presentation and fence summaries. Keep the interval matched between runs when comparing other changes.

`-TraceMovieAudio` measures Bink callback delivery without wrapping the title's Sleep import. To measure the recognized mixer's short waits, reuse the same published host and request the audit explicitly. A run with `-EnableMixerWaitAudit` forwards each wait to the original Sleep. A run with `-PreciseMixerWait` enables the same audit and uses a high-resolution timer for eligible 1–2 ms waits around active movie audio. Original Sleep remains the fallback.

```powershell
.\scripts\Invoke-R77.ps1 -OutputDirectory "$run-audited-wait" `
    -PublishedHostDirectory (Join-Path $run 'host') -TraceMovieAudio -EnableMixerWaitAudit
.\scripts\Invoke-R77.ps1 -OutputDirectory "$run-precise-wait" `
    -PublishedHostDirectory (Join-Path $run 'host') -TraceMovieAudio -PreciseMixerWait
```

Use the normal `-TraceMovieAudio` run and the audited wait run to measure observer overhead. Use the two audited runs to measure the timer change. Compare `mixerPreciseWaits`, `mixerPreciseFallbacks`, accepted PCM per second during active playback, coherent queue samples, and notes on the audible result. `-DisableMixerWaitAudit` remains available for older commands that explicitly request Bink-only tracing.

For a movie-worker scheduling comparison, use the exact same host. This optional mode promotes only the confirmed title movie worker.

```powershell
.\scripts\Invoke-R77.ps1 -OutputDirectory "$run-movie-scheduling" `
    -PublishedHostDirectory (Join-Path $run 'host') -TraceMovieAudio -EnableMovieScheduling
```

`[movie-audio]` reports the guest sound callback's readiness results, PCM bytes accepted at unlock, and delivery gaps. It samples refused calls for suspension, disabled state, and the last observed queue occupancy after the callback. Mixer activity can change occupancy between the refusal and observation. Confirm mixer consumption and endpoint padding separately if the producer stays active through audible gaps. The trace checks both original binary hashes before installing the callback observer.

Use `-TraceMovieAudio -TraceMovieEndpoint` in the local launcher to add the Windows spatial audio event trace for the verified title. A direct host launch uses `IRX_MOVIE_AUDIO_TRACE=1` and `IRX_MOVIE_ENDPOINT_TRACE=1`. Read `spatialOutput`, the deltas of `endpointSignals`, `endpointTimeouts`, and `endpointWaitFailures`, and the observed `mixedLane0QueuedBytes`. An active route with frequent event signals and repeatedly low mixed output while Bink remains queued points toward producer-to-mixer progress. Large signal gaps or wait failures warrant examining endpoint wakeups and scheduling. Queue snapshots can race the mixer, and an event signal does not establish playback at the speakers. If the spatial route is inactive, the log provides no event data for an ordinary output device or fallback backend. Keep the separate mixer Sleep audit off for this run.

With the endpoint trace enabled, `-EnableEndpointScheduling` compares the existing mixer policy applied to the verified spatial output worker. The launcher records this choice. Look for the worker match and MMCSS result before interpreting event rates. A direct host launch can enable it with `IRX_AUDIO_ENDPOINT_SCHEDULING=1`. For a matched control, reuse the same host and omit just the scheduling option. Compare audible gaps, endpoint signal cadence, accepted PCM per second, and host presents within the same active phase.

For a graphics log sampling comparison, reuse the same host and keep the movie scheduling setting constant. Use `-DisableGraphicsLogSampling` with a fresh output directory. Compare the same scene and use the same cold or warm shader cache condition. `IRX_VERBOSE_GRAPHICS=1` emits full success logs. Compare `IRX-R50-LOG` seen/emitted counts, presentation cadence, and matching upload and submission scopes separately. Those wall-time scopes overlap.

For an indexed staging comparison, use the default compact path first. Its `Indexed color compact upload` marker separates copied bytes from GPU reservation bytes. Set `IRX_COMPACT_INDEX_UPLOAD=0` for a comparison with the previous CPU staging path. Treat allocation metrics as cumulative and frame-time comparisons as valid only across comparable scenes.

The indexed UI path also reports `Indexed UI compact upload` for qualifying draws. The same control applies to both paths. To investigate missing glyphs, use `-TraceIndexedUiSources` or `IRX_INDEXED_UI_SOURCE_TRACE=1`. Look for `Indexed UI source` near the warning screen's present count. This records bounded texture/view metadata and source authority after live descriptor validation. Compare it with the screenshot and matching draw markers.

To measure native upload allocation overhead, compare `AllocateRectangleUpload` in the R25 timing summary across the same phase. Enable experimental pooling with `IRX_RECTANGLE_UPLOAD_LARGE_PAGES=1` and the normal rectangle page setting. This shares 4 MiB upload resources across eligible 256 KiB–2 MiB slices owned by one command-list recording. Compare deltas for slices, resources, and resource bytes in `Rectangle uploads`. Check `largeReuses` against `largePages` and `largeFallbacks` to see whether the policy packed multiple slices. Pooling can reserve unused space when lists record few eligible draws. Also compare working set and pacing to check whether the reduced resource creation paid off.

Use `-RecycleRectangleUploads` to test a separate pool across recordings. It returns eligible resources after successful command-list reset and proven fenced completion of all associated rectangle slices. Keep the large-page option fixed. Compare `recycledBuffers`, actual native allocations, allocation milliseconds per present, and pacing in the same UI phase. The idle pool is capped at 48 resources or 64 MiB. Direct host launches can enable it with `IRX_RECTANGLE_UPLOAD_RECYCLE=1`.

Compare `DetileTexture` and `UploadTexture` across matched movie and UI windows. `DetileTexture` measures CPU conversion from the console layout into the host upload footprint and is included in `UploadTexture`. Every copy still uses the validated source layout, correct mip or array slice, and existing queue ordering. Aggregate fence wall time per present and the native allocation stage are separate measurements.

### Measuring the 4K gameplay target

Use a repeatable gameplay scene at 3840×2160, record the guest timing mode, and compare distinct completed frames and frame-time percentiles across identical routes. The target is 60 FPS with steady pacing and continuous audio. That gives about 16.67 ms per frame. Startup presents can repeat or drop frames, and the observed title startup requests 30 Hz, so neither swapping at 60 Hz nor reporting the startup present counter establishes the target. Preserve a matching-symbol CPU trace and a GPU timeline to distinguish guest work, host submission, waiting, and GPU execution.

## Reporting a result

Provide the build commit, command line, environment switches, scene/action, log, and capture metadata. For network investigation, include the JSONL export and keep the raw ETL available. For CPU analysis, retain exact matching binaries and symbols. Report a successful build, component test, authored fixture, integrated title behavior, and performance measurement as separate observations.

Historical `IRX-R...` markers remain in logs for compatibility. They identify diagnostic families; current code and documentation are organized by responsibility rather than release number.
