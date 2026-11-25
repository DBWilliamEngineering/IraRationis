# Ira Rationis | Vindex

*The Wrath of Reason* · *Nullius in verba* · *Non impone opinionem*

**Defiance through competence.**

Independent engineering research: a C# Native AOT-oriented compatibility runtime derived from a source-level port of [Zettrox/ztrx](https://github.com/Zettrox/ztrx), based on VIctory and victory-fork. Upstream reference tree: `90b529f878a470fec9bf07b7be316bc769a12232`. Windows x64, C# 15, .NET 11, Native AOT. Output: `irx.exe`.

> **Experimental compatibility runtime.** Native title execution has been demonstrated in maintainer-reported local logs. Original game frames, intro playback, menus and gameplay have not been demonstrated. First Light has reached host GPU initialization, command-list creation, frame pacing, CPU-backed resource creation and descriptor heap initialization. The latest texture allocation fix and memory benchmark still require Windows validation. No game data is included. Run only trusted, lawfully obtained executables; IRX is not a sandbox.

## Engineering identity and differentiators

IRX is a GPL-3.0-or-later derivative of ztrx/VIctory, not a clean-room implementation or a claim of upstream feature parity. Its goal is a predominantly C# native compatibility runtime with explicit ABI boundaries, measurable memory behavior and systematic compatibility diagnostics. Independent work below denotes IRX-specific implementation or design relative to the upstream reference, not global novelty or a separate proprietary license.

| Area | IRX-specific work | Status |
| --- | --- | --- |
| Virtual unified memory (VUM) | Sparse 64 KiB software page table storing guest-to-CPU and guest-to-GPU translations plus residency strategy | Isolated proof of concept; not wired to title allocation or GPU submission |
| Memory strategy evaluation | Common workload comparing system upload, mirrored GPU-local memory, CPU-visible GPU upload, and existing system-memory heaps; checksum validation and timing | Benchmark implementation; Windows results pending |
| Managed graphics ABI | C# unmanaged object layouts and direct COM dispatch through TerraFX; console and host interfaces kept separate | Partial title startup observed in Debug/JIT; published AOT validation pending |
| Generated ABI fallbacks | Roslyn incremental generator emits 160 fallback callbacks and indexed function-pointer lookup at build time | Maintainer build and execution reported; known registrations remain explicit |
| Compatibility preflight | Build hash, import-slot inventory, named graphics ABI coverage, status classification, persisted blocker history | Preflight observed in local execution; not a complete static call graph |
| Synchronization | Conservative D3D12 enhanced global barrier for console pipeline synchronization rather than the upstream no-op | Implemented; exact console semantics and cross-queue coherence not established |
| Testing and diagnostics | Synthetic GPU presentation, memory checksum benchmark, graphics ABI probes, allocation tracing and native breakpoint logging | Implemented; individual Windows tests require validation |

### Virtual unified memory: research architecture

The console presents a unified address space to CPU and GPU. Discrete Windows GPUs expose different physical backing and virtual address domains. IRX explores a software translation layer that separates guest-visible identity from host CPU and GPU virtual addresses and records the selected backing strategy. The 64 KiB page table supports map, translate and unmap; it does not program hardware page tables, intercept arbitrary native CPU loads/stores or make CPU pointers GPU-visible by itself.

The four benchmark strategies are:

1. `SystemUpload`: CPU writes a mapped upload resource and the GPU reads through its system-memory mapping.
2. `MirroredLocal`: cached system memory is staged through an upload resource and copied to GPU-local memory.
3. `GpuUpload`: CPU writes a mapped GPU_UPLOAD resource where supported, using CPU-visible VRAM without a separate staging copy for the benchmark input.
4. `ExistingSystemHeap`: opens eligible existing system memory as a D3D12 heap, subject to driver restrictions.

Each uses the same full-buffer write and GPU checksum workload, with CPU write/read sampling, copy and compute timestamps, submission/fence and end-to-end timings. Adaptive policy, dirty-range ownership, fence-controlled synchronization, migration and production address translation are future work. Current XMem aliases and CPU-backed resource views are separate production paths, not proof that VUM is integrated. No speedup, reduced PCIe traffic or zero-copy gameplay is claimed.

Upstream already has substantial memory research: [inplace.rs](https://github.com/Zettrox/ztrx/blob/main/crates/victory-gpu/src/inplace.rs) explores opening title memory directly; [shadow.rs](https://github.com/Zettrox/ztrx/blob/main/crates/victory-gpu/src/shadow.rs) implements persistent GPU copies and writeback; [watch.rs](https://github.com/Zettrox/ztrx/blob/main/crates/victory-gpu/src/watch.rs) tracks writes at 64 KiB granularity. IRX's distinct contribution is its explicit translation/residency model and comparative benchmark harness, not the invention of shared memory, page tracking or mirroring.

### Provenance and comparison

The native PE loader, TLS redirection, AMD instruction emulation, console ABI signatures, GameInput, DirectStorage, XMem page model, descriptor behavior, and RDNA2 texture sizing are adapted from or guided by upstream. The latest texture sizing code retains upstream and AMD addrlib attribution. IRX is not yet equivalent to upstream's renderer, shader translation, optimized memory synchronization or demonstrated gameplay. No percentage of original code is asserted: language changes, generated source and rewritten implementations do not measure algorithmic originality. A complete independent audit of competing runtimes has not been performed.

### Tracker-readable project status

| Field | Value |
| --- | --- |
| Project | Ira Rationis (IRX) |
| Release | Vindex v0.1.0 / First Light development |
| Implementation | C# 15 / .NET 11; Windows x64; Native AOT target; minimal native exception handling and experimental probe backends |
| Relationship | Source-level derivative of ztrx/VIctory; independent IRX research extensions |
| Unique research focus | Software address translation and multi-strategy CPU/GPU memory benchmarking; generated ABI fallbacks and automatic per-build compatibility preflight |
| Production VUM | Not integrated |
| Latest verified run mode | Maintainer-reported Debug/JIT (WinDbg loaded CoreCLR and CLR JIT) |
| Current milestone | Exe runs; partial platform and graphics initialization |
| Progress | 10% provisional self-assessment, not independently assigned |
| First original frame / intro / menu / gameplay | Not demonstrated |
| Performance | No measured title FPS or comparative speedup |
| Latest blocker | Texture allocation assertion; sizing fix implemented, not yet validated on Windows |

## GTA VI compatibility progress

**Last updated: 2026-11-25**  
**Release:** Vindex v0.1.0 — First Light (development)  
**Current milestone:** Exe runs (native entry point reached; partial import coverage)  
**Self-assessed progress:** 10% provisional on the 0–100 milestone scale (not a tracker-assigned score)  
**Next milestone:** First frame (20%) — original title logo, loading screen or intro video rendered by IRX.

![Progress: 10% provisional](https://img.shields.io/badge/progress-10%25%20provisional-805ad5) ![Milestone: Exe runs](https://img.shields.io/badge/milestone-Exe%20runs-blue) ![Gameplay: Not demonstrated](https://img.shields.io/badge/gameplay-not%20demonstrated-lightgrey)

### Latest execution evidence (2026-11-25)

Maintainer-reported execution: graphics frame interval configured to 33,333 microseconds (30 Hz), period 3; another 2,960 physical pages (185 MiB) reserved; device slot `0x428` subsequently created a CPU-backed buffer view at an existing virtual address. This is startup evidence, not a measured frame rate. No original title frame or intro has been displayed.

The maintainer's latest execution confirms ABI preflight, frame pacing, CPU-backed buffer views and creation of all four descriptor heap types. WinDbg identified an assertion after device slot `0xC8` (`GetResourceAllocationInfo`); allocation diagnostics confirmed that a 3840 × 2160 RGBA8 texture was rejected with the invalid-size sentinel. This revision ports upstream console texture sizing for supported formats, tile dimensions, mip tails, and depth/stencil planes and reuses it for CPU-backed resource allocation. The new implementation remains pending Windows validation. Compression metadata, texture byte addressing, GPU-visible backing and presentation are outstanding; overall milestone remains 10% provisional.

### Milestone ladder

| Milestone | Score | Current evidence |
| --- | ---: | --- |
| Groundwork | 0–10% | Partial loader/HLE, shader-container parsing and graphics ABI implementation |
| Exe runs | 10% | Maintainer-reported: original title entry point reached; startup currently stops at a texture allocation assertion (fix pending validation) |
| First frame | 20% | Not demonstrated; synthetic presentation probe is not title rendering |
| Main menu | 30% | Not reached |
| In the world | 40% | Not reached |
| Playable | 50% | Not reached |
| Playing well | 50–100% | Not eligible; story progress, rendering fidelity, gameplay FPS and stability unverified |

This is a cumulative milestone ladder, not a source-code completion estimate. A tracker may score the current executable milestone as partial because imports remain unresolved. No points are claimed for untested functionality or for results achieved by upstream projects.

### Groundwork breakdown

| Category | Maximum weight | Evidence / current status |
| --- | ---: | --- |
| Loader & system libraries | 2 pts | Native PE mapping, TLS redirection, dependent DLL initialization and entry-point execution observed; platform HLE incomplete |
| Shader translation | 2.8 pts | DXBC/DXIL and SGD5 parsing implemented; zero title shaders verified as translated by IRX; actual coverage unknown |
| Graphics layer | 3.2 pts | Partial console device, queue, allocator and fence ABI backed by host D3D12; C# device, queues, command-list lifecycle, frame interval observed; CPU-backed buffer view and descriptor heap creation observed; new texture sizing fix pending validation; GPU resource translation, shader pipelines and PresentX outstanding |
| Proven on another game | 2 pts | No other retail Xbox Series game demonstrated by IRX; synthetic fixtures do not count |

No numerical completion percentages are assigned to individual groundwork categories without a measured denominator. The weights above follow the supplied 10-point groundwork methodology.

### What works (maintainer-reported execution evidence)

- The main title PE image and four dependent native DLLs mapped successfully.
- 53,246 static TLS load locations redirected; the four DLL attachment calls returned.
- Native CPU exception handler installed; the prior managed reverse P/Invoke termination did not recur on that path.
- Main-image TLS stage completed and the original native entry point was called.
- GameInput and DirectStorage factories initialized, 12 title archives opened, 11,360 physical pages (710 MiB) reserved, and the NVIDIA GeForce RTX 4090 host D3D12 device created. The subsequent heap ABI revision created five queues and stopped at device slot `0x248`.

Sanitized excerpts from local execution reports, 2026-11-24:

```text
TLS loads redirected: 53246
[launch] Native CPU exception handler installed.
[launch] Attached.
[launch] Main image TLS callbacks.
[launch] Calling native entry point.
Unimplemented import: GameInput.dll!#1
```

Subsequent run:

```text
Unimplemented import: XCRDAPI.dll!DStorageGetFactory
```

Earlier graphics run:

```text
[input] GameInput initialized.
[dstorage] Factory created.
[dstorage] Opened G:\movies.rpf.
[xmem] Allocated 11360 physical pages (710 MiB reserved, flags 0x280).
[graphics] Host device created: NVIDIA GeForce RTX 4090
[graphics] Unsupported console graphics object slot 0xd0
```

Latest reported run after the heap ABI hotfix:

```text
[graphics] Host device created: NVIDIA GeForce RTX 4090
[graphics] Command queue type 0 created.
[graphics] Command queue type 2 created.
[graphics] Command queue type 2 created.
[graphics] Command queue type 2 created.
[graphics] Command queue type 2 created.
[graphics] Unsupported console graphics device slot 0x248
```

These are maintainer-reported console excerpts, not an independently reproduced test or published video. The earlier excerpts do not establish build mode; the latest WinDbg session loaded CoreCLR and CLR JIT, confirming a Debug/JIT run rather than a published Native AOT run. Local hardware reported for development: Intel Core Ultra 9 285K, NVIDIA GeForce RTX 4090 (24 GB), 64 GB system RAM. No title FPS measurement is available.

### Current blockers and next steps

- DirectStorage factory initialization and asset archive opening confirmed; actual streaming/decompression coverage remains unverified. BCPack and full priority scheduling are outstanding.
- Host D3D12 device and five command queues created. The heap ABI hotfix has progressed beyond `0xD0`. Subsequent C# revisions passed command-list creation, frame interval and descriptor heap creation in maintainer-reported Debug runs. The latest failure is the texture allocation assertion; the sizing fix awaits Windows validation. The host presentation probe does not yet connect to title rendering.
- Console command-list recording and translation, resource/descriptor translation, texture layout (XG), shader lowering/emission, guest PresentX, and audio (XApu/AcpHal) are incomplete.
- First Light acceptance: original introduction frames displayed with correct colors, aspect ratio, source cadence and synchronized audio. Record measured frame timing and evidence before advancing the milestone.
- Virtual unified memory (VUM) is an independent experiment; no performance advantage over upstream or competing runtimes has been demonstrated.

### Performance and stability

| Metric | Result |
| --- | --- |
| Original intro FPS | Not measured |
| Gameplay average FPS | Not measured |
| Sustained 30 FPS gameplay | Not demonstrated |
| Rendering fidelity | Unverified; no original game frames |
| Story progress | Not reached |
| Crash-free gameplay session | Not demonstrated |
| Synthetic GPU presentation / memory POC | Implemented, awaiting Windows measurements; excluded from gameplay scoring |

### Progress history

| Date (US Central) | Milestone / revision | Evidence |
| --- | --- | --- |
| 2026-11-24 | Native execution | Mapping, TLS redirection and import binding reported; reverse P/Invoke crash |
| 2026-11-24 | Native interoperability | Native VEH installed; DLL attach and title entry-point execution reported; GameInput import blocker |
| 2026-11-24 | Input / physical memory | Later execution reported DirectStorage factory as the next missing import |
| 2026-11-24 | DirectStorage | Factory, queues, raw/zlib reads and physical-page writes implemented; runtime validation pending |
| 2026-11-24 | First Light | Host GPU and five command queues confirmed by maintainer log; stopped at device slot 0x248 |
| 2026-11-24 | Managed graphics | C# device and command-list lifecycle implemented |
| 2026-11-25 | ABI preflight | Maintainer observed import counts and registered graphics ABI coverage before loading |
| 2026-11-25 | Frame/resource/descriptor ABI | Command lists, 30 Hz requested frame interval, CPU-backed buffer view and all four descriptor heap types observed |
| 2026-11-25 | Texture sizing | WinDbg and allocation trace identified rejected RGBA8 texture; upstream-derived sizing fix implemented, Windows verification pending |

Update this section when new reproducible results are obtained. Record the commit SHA, test date, host hardware, build mode, exact milestone, sanitized log or screenshot/video link, and any remaining blockers. Do not include title binaries, assets, keys, private paths or personal information. Upstream progress is not IRX progress.

## Build and test

Use a Windows machine with .NET 11 SDK, Visual Studio Desktop development with C++ and the Windows SDK.

```powershell
dotnet restore IraRationis.slnx
dotnet build IraRationis.slnx -c Release
dotnet run --project IraRationis.Runtime.Tests -c Release --no-build
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native
python tests/smoke.py artifacts/native/irx.exe
# In an x64 Visual Studio Developer PowerShell:
./tests/native/smoke.ps1 artifacts/native/irx.exe
```

## Run

Launch the published executable without arguments to select a title:

```powershell
.\artifacts\native\irx.exe
.\artifacts\native\irx.exe run
.\artifacts\native\irx.exe run "D:\Games\title.exe"
.\artifacts\native\irx.exe "D:\Games\title.exe"
```

The native picker and the explicit path converge on `RunCommand.Execute`.
The default missing-import policy traps with the DLL and symbol name. The
optional `--unimplemented zero` matches the upstream bring-up option but
can lead to invalid state or later faults. Do not use it for the initial run.
`--bind-only` maps prepared images and resolves imports without intentionally
calling title entry points or TLS callbacks. It is not a security sandbox.
`--console series-s` selects Series S; Series X is the default. `--strict`
disables host pass-through.

```powershell
.\artifacts\native\irx.exe run "D:\Games\title.exe" --bind-only
.\artifacts\native\irx.exe inspect
.\artifacts\native\irx.exe shader "D:\Shaders\example.dxbc"
.\artifacts\native\irx.exe archive "D:\Shaders\example.sgd5"
```

The synthetic native probe expects exit code 42 from the published AOT
executable and checks module identity, worker creation and IAT binding.
It requires an initialized MSVC x64 developer environment. It does not
validate the game, all TLS paths or Direct3D 12. The component tests also
include TLS byte-patch regression cases. Neither test has been run here.

## Dependencies

Central package versions are in `Directory.Packages.props`. `Blake3` 3.0.2 (managed, not `Blake3.Native`) is referenced by Loader and Pipeline. `Tomlyn` 2.10.1 is referenced by Core using generated metadata. TerraFX.Interop.Windows 10.0.26100.6 is referenced by the managed graphics project. Microsoft.CodeAnalysis.CSharp 4.14.0 is a build-time generator dependency, not a shipped runtime component. Win32 uses `LibraryImport`. No Rust runtime dependency. CsWin32 and Opus implementation selection remain deferred until their actual port work. The SDK and NuGet feed are unavailable in the generation environment, so these choices require restore and publish verification.

Configuration retains the upstream `victory.toml` layout and `VICTORY_*` environment variables, with `IRX_*` taking precedence. Default data/cache directories are under local app data `IraRationis`. Copy `victory.example.toml` to `victory.toml` to configure it. The generated TOML reader has not been checked against the upstream strict unknown-field rejection. The repository marker guard is best effort and is not a sandbox or security boundary.

## Development history and implementation notes

The following notes describe successive historical revisions; the dated progress section above is authoritative for current milestone claims. Later implementations supersede earlier lists of missing modules.

### Foundation and native execution

This revision preserves existing PE/DXBC/DXIL inspection, HLE registry/host policy, title state, native picker, console memory configuration, and AMD instruction decoder. It adds in-memory image preparation, BLAKE3-based conversion cache, SGD5 record scanning and bounded raw-deflate extraction, build identification, and configuration/policy. Synthetic tests exercise these additions. The .NET inflater may not reject malformed streams identically to miniz_oxide; verify using upstream fixtures. Image preparation rejects certain malformed relocation/terminator data that upstream tolerates. Cache publication tolerates a concurrent successful writer and uses an IRX-specific prepared image name. No feature-parity claims are made.

This revision adds `LoadedImage`, `TitleImages`, `ImportThunks`, `TlsRedirector`, `TitleThreads`, `ConsoleInstructionHandler`, `KernelImageModule`, `KernelThreadModule`, and `RunCommand`. It uses source-generated Win32 declarations, not dynamic managed code generation. The default launch path maps a cache copy, binds ordinary and delayed imports, initializes title DLLs and TLS callbacks, and calls the original entry point on a thread. Game-derived files remain outside the repository.

### Known gaps at the native-execution revision (historical)

- Thread notifications are explicit for the main title thread and intercepted CreateThread workers. The original linker-installed process-wide TLS callback is not ported: managed callbacks under the loader lock are not assumed safe. CRT-created threads, native pool workers, dynamic title DLL loads and thread exits that bypass the wrapper are not covered. Native process-detach choreography is not implemented; images and thunk memory remain mapped until process exit.
- Forwarded title exports are treated as unresolved rather than allowing the OS to load an unprepared DLL. Dynamic LoadLibrary/GetProcAddress interception, virtual drive mounting, networking restrictions, and other production HLE modules are not implemented. Host pass-through is not isolation. Keep the runtime offline during bring-up and only use trusted executables.
- TLS redirection uses the upstream byte-pattern approach, not full instruction decoding. The x64 CONTEXT offsets and exception callback path need Windows validation. The handler re-queries memory for CLZERO and does not implement upstream region caching or CLWB self-patching. It emulates the decoder's supported instructions only within initially mapped title images.
- Crash-reporting/unwinding infrastructure is not yet ported. Default missing import thunks report the name and fail fast; debugger integration and persisted crash logs are outstanding.
- GPU translation, shader lowering/emission, texture/audio codecs and the remainder of the HLE implementation remain outstanding. The title may fail before opening any game window. A successful native probe is not gameplay parity.
- A solution-local NuGet.config is included to prevent the NU1507 error encountered with multiple inherited package feeds. The previous baseline built on the user's machine; the new files still require compilation, AOT publication and execution testing.

Preserve upstream behavior, including optional runtime code, until testing demonstrates safe removal. Projects remain directly beneath the solution root; logical grouping is via solution folders. No migration manifest is included.

## Release identities

| Version | Codename | Planned milestone |
| --- | --- | --- |
| 0.1.0 | Vindex | Foundation |
| 0.2.0 | Abaddon | Native interoperability |
| 0.3.0 | Apollyon | Compatibility and execution |
| 0.4.0 | Prometheus | Diagnostics and performance |
| 0.5.0 | Praetor | Consolidation |
| 1.0.0 | Invictus | First stable release |
| 2.0.0 | Aeternum | Next-generation architecture |

## License

The adapted software is GPL-3.0-or-later; see `LICENSE`. Preserve upstream attribution. `LICENSE-PROPRIETARY.md` only covers separately identifiable, independently owned material outside GPL coverage. Do not commit game files, extracted assets, or derived caches.

## Native interop revision

The previously managed vectored exception handler has been replaced by a small x64 C++ DLL (`irx-native.dll`). It uses the Windows SDK's `CONTEXT`, accepts only illegal-instruction exceptions within the initial mapped-image ranges, and never calls managed code. The DLL stays loaded for the process lifetime; disposal disables the handler without unloading code that may still be in use. The decoder and bit-field routines are compiled and tested as part of the MSVC build. The handler's one-line CLZERO strategy preserves the original loop's flags but does not yet include upstream's bulk-zero acceleration or region caching. The handler is limited to the initially mapped images, not dynamically loaded images. There is no CLR internal API dependency.

The CLI MSBuild target builds the native component using the Visual Studio Desktop development with C++ workload (x64 MSVC and Windows SDK). Install the workload if needed. Both normal builds and Native AOT publishing copy `irx-native.dll` beside the executable. The DLL is intentionally a sidecar for now, not a static library. The C# code remains Native AOT-compatible in design. `dotnet build` and `dotnet publish` still require local validation.

```powershell
dotnet restore IraRationis.slnx
dotnet build IraRationis.slnx -c Release
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native
.\artifacts\native\irx.exe veh-probe
# From x64 Visual Studio Developer PowerShell:
.\tests\native\smoke.ps1 artifacts/native/irx.exe
```

`veh-probe` registers the native handler and throws/catches a managed exception, without a title executable. It is an isolation probe, not proof of full instruction-emulation or title compatibility. Native code mapping and HLE work should be tested only with trusted, lawfully obtained executables; the runtime is not a sandbox. Do not run unknown or leaked binaries. No game files are included.

### Additional HLE modules at the native-interop revision (historical)

- XMem: general/default allocations and frees, allocation hooks, constrained virtual allocation, virtual query, retail tools-memory behavior, working-set reporting and memory transfer. Physical-page allocation, alias mapping, scatter mapping and GPU memory tracking remain absent; placeholder replacement explicitly fails instead of pretending to map pages. The allocation hook path is untested. Host allocation and file handles retain host semantics where appropriate.
- Files: virtual `G:` package and `T:` temporary mounts, path translation, common ANSI/UTF-16 Win32 open/query/mutation/enumeration calls. CRT path wrappers and specialized read behavior remain unported. Relative components are lexically clamped to the mount root, unlike upstream's short-path behavior. This is not filesystem isolation.
- D3D12: virtual memory configuration, versioned and legacy root-signature serialization (masking console-only flags) and host root-signature deserialization. Console device creation and all GPU object translation remain unimplemented; no claim of graphics compatibility.
- DSP: connection/stream management and synchronous command completion with silent output, consistent with upstream's current limitation. XMA, XApu and AcpHal are not included in this revision.

The existing loader/kernel modules are retained; dynamic title DLL loading, complete thread lifecycle, XG texture computer, DirectStorage and GameInput remain incomplete. New managed regression tests cover XMem alignment/range calculations, virtual drive mapping and DSP lifecycle. The portable native decoder passed eleven assertions under Clang with AddressSanitizer and UndefinedBehaviorSanitizer; the Windows handler, MSVC compilation, C# compilation and AOT publication were not executable in the preparation environment. The supplied CI runs those checks on Windows. The native handler addresses the suspected reverse-P/Invoke failure; a successful `veh-probe` and subsequent execution trace are needed to determine whether it fixes the observed launch crash.

Upstream credits: Zettrox/ztrx (GPL-3.0-or-later), with its original console instruction decoder attribution to ivall/xcl (identified by upstream as GPL-2.0-only). The source license compatibility of those contributions should be clarified before distributing a combined work; this revision does not purport to relicense third-party contributions.

## Experimental unified-memory benchmark

`IraRationis.Memory` is a Native AOT-compatible C# experiment with a sparse
64 KiB software page table. It tracks guest, CPU and GPU virtual addresses;
it does not alter hardware page tables or replace the title allocator yet.
`MemoryProbe.cpp` is a small Direct3D 12 benchmark backend in the existing
native DLL; no proprietary assets are required. Build with the x64 MSVC tools
and a recent Windows SDK (10.0.26100 or newer headers recommended for
`D3D12_HEAP_TYPE_GPU_UPLOAD` and OPTIONS16). No additional NuGet packages.

```powershell
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native
.\artifacts\native\irx.exe memory-probe --self-test
.\artifacts\native\irx.exe memory-probe --mib 64 --iterations 10
.\artifacts\native\irx.exe memory-probe --iterations 10 | Tee-Object memory-results.csv
```

Default sizes: 16, 64, 256 MiB. `--adapter N` selects an adapter in DXGI
high-performance preference order (default 0). Check the printed adapter name.
The GPU_UPLOAD feature query must succeed; unsupported drivers are skipped.
ExistingSystemHeap is an optional driver-dependent OpenExistingHeapFromAddress
probe and may be refused; a failure does not fall back silently to a copy.

| Mode | CPU writes | GPU reads |
| --- | --- | --- |
| SystemUpload | Mapped UPLOAD resource | System RAM via GPU mapping |
| MirroredLocal | Cached VirtualAlloc RAM, then memcpy to UPLOAD | DEFAULT VRAM after explicit GPU copy |
| GpuUpload | Mapped GPU_UPLOAD resource | Same CPU-visible VRAM allocation |
| ExistingSystemHeap | Cached VirtualAlloc RAM | Placed cross-adapter buffer over existing heap |

All modes write the same changing pattern, then run the same HLSL compute
checksum covering every input dword. A small readback verifies each 16 KiB
block. One warmup per mode/size is excluded. GPU copy and compute times are
GPU timestamps; submit/wait includes command submission, output readback copy,
and fence wait. Cycle includes CPU page-table lookup and writes, staging,
command recording, GPU work, waiting and checksum validation. The independent
random CPU read sample is amortized time per read (includes loop overhead,
not serialized hardware latency). It is excluded from cycle time. Mode 1
copies the entire buffer on purpose; it is not a reproduction of upstream's
optimized dirty-page algorithm. The probe measures synchronous microbenchmarks,
not game FPS, streaming overlap, GPU writes, texture layout, memory aliasing,
residency migration or competing runtimes. GPU_UPLOAD is not suitable for
arbitrary GPU-writable or CPU-read-heavy title memory. Host pointers cannot
be assigned arbitrary guest virtual addresses with this API.

The experiment is intentionally isolated from XMem and the title loader.
No Windows compiler, D3D12 driver or .NET SDK was available when this revision
was prepared; Windows compilation and all GPU measurements remain unverified.


## Startup revision: GameInput and physical pages

This revision retains the existing native VEH and all four memory-probe strategies unchanged. It adds `GameInputModule` and `PhysicalPageModule` to the launch registry. GameInput implements the upstream ordinal 1 and the named alias, the 22/21/25-entry input/device/reading vtables, XInput, focus-sensitive keyboard input and a fallback pad, device enumeration/callbacks, 32-entry device reading history, and counted reading references. As upstream, reading/guide/layout callbacks are not delivered, unsupported device methods return E_NOTIMPL, and QueryInterface returns E_NOINTERFACE. Unlike upstream, each returned reading has its own native wrapper and managed handle; this may increase allocations. The input worker is process-lifetime, and title-wide thread notifications are still incomplete.

Physical pages use pagefile-backed SEC_RESERVE sections, 64 KiB page IDs, split placeholders, MapViewOfFile3, per-view commit, scatter and alias mapping. Consecutive pages in one section are grouped where possible; previously split placeholders may fall back to per-page views. No copying occurs between aliases. Existing views keep their section alive after page IDs are freed. Remaps are serialized within this module, but are not transactional if a Windows API fails after partially changing the views; concurrent native readers and future GPU submissions are not synchronized yet. Wine-specific SEC_COMMIT behavior, DirectStorage physical-page writes, GPU dirty-page notifications and production VUM backing integration are not implemented. XMem placeholder replacement follows upstream's unchanged-placeholder behavior.

Run synthetic validation, from x64 Visual Studio Developer PowerShell:

```powershell
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native
.\artifacts\native\irx.exe veh-probe
.\tests\native\input-memory.ps1 artifacts/native/irx.exe
.\artifacts\native\irx.exe memory-probe --self-test
.\artifacts\native\irx.exe memory-probe --mib 64 --iterations 10
```

The native probe builds its own ordinal import library and synthetic title, checks GameInput enumeration, reading layout and release, then allocates two pages, maps them at two addresses, verifies writes through aliases and scatter remapping, frees IDs while views remain readable and unmaps the views. Expected exit code 42. The Windows build and probe have not been executed in the preparation environment. The managed regression checks also validate ABI sizes and host-to-console button conversion.

At the input revision, Direct3D12.X device creation, texture layout, DirectStorage, XApu and AcpHal were incomplete. Later sections describe subsequent changes; intro-video and gameplay parity remain unproven. The memory POC remains independent rather than replacing the production allocator. Use only trusted, lawfully obtained test executables; this is not a sandbox.

## DirectStorage bring-up

`DirectStorageModule` registers the console factory export and its observed ABI from upstream `dstorage.rs`. Implements file, queue and status-array objects, ordered per-queue background workers, capacity/backpressure, half-full submission, staged byte budget, raw file and memory reads, bounded zlib decompression, and writes to section-backed physical pages (including unmapped pages). Existing memory-probe commands remain unchanged.

This is partial parity: BCPack and its tables, priority-weighted parallel scheduling, direct decompression into physical views, and GPU dirty tracking are not implemented. Unsupported request formats return an error status and log the options; do not treat this as successful asset loading. File/state references are retained through queued work. Queue close cancels unstarted statuses and waits for active work. Windows compilation and execution are unverified here.

Synthetic test, from x64 Visual Studio Developer PowerShell:

```powershell
./tests/native/storage.ps1 artifacts/native/irx.exe
```

Expected exit code 42; verifies factory IID, native vtable layout, raw memory and file reads, zlib, physical-page writes and ordered status/error delivery.

## First Light experimental graphics revision

The DirectStorage revision is retained, including `memory-probe`. The newly
registered `D3D12XboxCreateDevice` export creates a real hardware D3D12 host device
and returns a separate console-layout object (0x58 bytes, 160-slot vtable).
This is **not** an Xbox-compatible renderer yet. It implements a small subset of
object methods, host feature queries, queue and allocator creation, allocator
reset, fence creation, CPU signal and event completion, and queue signal/wait.
The older and newer queue ABI offsets are distinct. GPU-addressable console
fence values, DXGI console interfaces, graphics command recording, descriptors,
resources, pipeline and shader conversion, guest PresentX and media/audio are
still missing. Unsupported vtable slots terminate with their offset; they do
not return fabricated successful results. Host feature answers are provisional
and differ from the upstream synthetic console feature set. Direct inlined
fence-value reads may be stale; do not assume console fence coherence.

The graphics ABI subset now lives in C# in IraRationis.Graphics. The native DLL still contains the VEH and the independent memory/presentation probes, built automatically by the existing MSVC build script.

Independent tests (no title or assets needed):

```
irx.exe graphics-probe
irx.exe present-probe --seconds 15 --fps 60
irx.exe present-probe --seconds 15 --fps 60 --gpu-upload
irx.exe memory-probe --mib 64 --iterations 10
```

`graphics-probe` checks both device/queue layouts, heap properties, queue description, command-list creation, close, empty submission, allocator/list reset and an actual host queue fence reaching 42. `present-probe` opens a 1280x720 native window,
creates a flip-model swap chain, produces synthetic RGBA frames, copies them
from persistently mapped upload buffers to back buffers and presents with vsync.
It reuses two allocators/buffers using per-buffer fence values and reports CPU
fill, fence and Present-call timing and submitted FPS. It does not report actual
scanout cadence or video decode correctness. The optional GPU_UPLOAD heap is
checked for driver support, and still copies to the swap-chain back buffer.
Neither command renders the original title intro or validates the title's
rendering path. The memory POC remains independent of title XMem allocation.

Validation: the existing portable instruction decoder passed 11 assertions
with Clang ASan/UBSan. The Windows native graphics code and C# tests could not
be compiled here (no MSVC/Windows SDK/.NET SDK or Windows GPU). Run the probes
on Windows before evaluating the new title-facing ABI subset.

## Managed graphics migration (Vindex / First Light)

Maintainer-reported runtime evidence: native entry point, GameInput, DirectStorage archive opens, 710 MiB of physical pages, RTX 4090 device and five command queues created. Last observed failure: device slot `0x248` (`CreateCommandListX`). Overall self-assessed milestone remains provisional 10%: no original frame yet.

This revision moves the production console graphics device, queues, allocators, fences and command-list lifecycle into `IraRationis.Graphics/ConsoleGraphicsDevice.cs`. It references TerraFX.Interop.Windows for the host D3D12/DXGI types; raw unmanaged COM vtable calls preserve ABI and avoid runtime COM marshalling. `CreateCommandListX` uses the upstream 16-byte description and a console-visible list prefix, backed by a real host command list. Old/new list reset and queue execution slots are installed. `graphics-probe` tests creation, close, submission, a real fence, allocator reset and list reset for both layouts.

The solution folder `IraRationis.Native` contains all remaining native source files; the CLI MSBuild target remains the only native build owner. `GraphicsDevice.cpp` has been removed from the build. If applying an overlay, delete the old file after checking local changes. C++ presentation and memory benchmark backends remain until separately migrated; the VEH remains native by design.

Limitations: not Windows-compiled here; actual graphics workload remains unimplemented (resources, shader pipelines, descriptors, draw/dispatch, console fence-memory coherence, frame-token semantics and title presentation). The empty command-list probe is not game rendering. Unsupported slots fail fast with the offset and object type when either leading argument identifies a live console object; unknown ABI calls may still be unidentified. Host submissions are asynchronous; callers must retain objects and obey normal D3D12 fence/allocator lifetimes. The experimental VUM is unchanged.

## Resource ABI and preflight revision

- Device `0x428`: create an unmanaged resource object over the existing title virtual address; no copy, no ownership of borrowed storage. CPU map/description/address/heap-property queries are supported for the known ABI. No host GPU resource or swizzle conversion is created by this path yet.
- Device `0xC8`, `0xD8`, `0xE0`, `0xE8`, `0x260`, `0x3F8`: CPU buffer footprint, aligned heap storage, placed and committed buffers. Unsupported texture footprints/committed textures return failure rather than inventing console tile sizes. Enhanced layout/state and castable-format semantics are not implemented.
- Device `0x250` and `0x258`: register/unregister logical page pools, not GPU tile mapping.
- Build-time `IraRationis.Generators` generates fallback entry points; no Roslyn runtime dependency. Known ABI registrations remain explicit, and the named inventory is derived from upstream tables, not a complete ABI specification.
- On selection, preflight constructs graphics tables before image mapping and writes `cache/compatibility/<build-hash>/graphics-abi.tsv`. Unsupported calls append to `blocking-slots.tsv` before fail-fast. On later starts, previously observed slots are compared against the current table. Registration is not verification; uncalled or unknown methods are not necessarily title requirements.
- `graphics-probe` includes resource CPU aliasing, borrowed lifetime, aligned heap/buffer allocation and page-pool tests. Existing synthetic memory/presentation probes are unchanged.
- Windows build, Native AOT publication and runtime verification pending. Tracker milestone remains 10% provisional.


## Texture footprint revision (current)

The latest patch adds `Textures/TextureFootprintCalculator.cs`, adapted from upstream `victory-formats/src/texture.rs`, and shares its sizing path across resource allocation and CPU-backed texture creation. Supported layouts include linear and tiled sizes, mip tails, array and volume textures, block compression, depth/stencil planes and tiled MSAA. For the observed 3840 x 2160 RGBA8 single-mip render target, the upstream algorithm selects 64 KiB R_X tiles and calculates 33,423,360 bytes with 65,536-byte alignment (compression metadata excluded). The original failure sentinel was `0xFFFFFFFFFFFFFFFF`. No hardware validation, texture byte addressing, GPU resource backing or rendering is implied. The new regression cases have not been executed on Windows.
