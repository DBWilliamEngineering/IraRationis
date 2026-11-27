<div align="center">
  <img src="docs/assets/crest.png" width="260" alt="Ira Rationis crest" />
  <h1>IRA RATIONIS</h1>
  <h3>PROJECT VINDEX · v0.1.0</h3>
  <p><i>The Wrath of Reason</i><br />
  <b>Nullius in verba · Non impone opinionem</b></p>
  <p><b>Defiance through competence.</b></p>
  <p>
    <img alt="Windows x64" src="https://img.shields.io/badge/WINDOWS-x64-333840?style=for-the-badge" />
    <img alt="Experimental" src="https://img.shields.io/badge/STATUS-EXPERIMENTAL-333840?style=for-the-badge" />
    <img alt="Initial settings UI reached" src="https://img.shields.io/badge/MILESTONE-SETTINGS%20UI-28704a?style=for-the-badge" />
    <img alt="C# first" src="https://img.shields.io/badge/C%23-FIRST-6942b8?style=for-the-badge" />
  </p>
  <p>
    <img alt="C# 15" src="https://img.shields.io/badge/C%23-15-6942b8" />
    <img alt=".NET 11" src="https://img.shields.io/badge/.NET-11-6942b8" />
    <img alt="Native AOT" src="https://img.shields.io/badge/Native-AOT-37698c" />
    <img alt="Direct3D 12" src="https://img.shields.io/badge/Graphics-D3D12-37698c" />
    <a href="https://github.com/DBWilliamEngineering/IraRationis/actions/workflows/build.yml"><img alt="Windows x64 Native AOT CI" src="https://github.com/DBWilliamEngineering/IraRationis/actions/workflows/build.yml/badge.svg?branch=main" /></a>
  </p>
  <p>Native console compatibility research<br />
  Windows x64 · C# · Native AOT · Direct3D 12<br />
  Updated November 26, 2026 · R77 · Initial settings UI reached<br />
  Next development target: R78 · Main menu transition</p>
  <p><a href="#current-status">Status</a> · <a href="#architecture">Architecture</a> · <a href="#build-and-validate">Build &amp; validate</a> · <a href="#documentation">Documentation</a> · <a href="#distribution-and-releases">Releases</a> · <a href="#licensing-and-attribution">Licensing</a></p>
</div>

---

Ira Rationis (IRX) is an experimental native x86-64 Xbox Series X|S compatibility runtime for Windows. It develops its own architecture for loading, platform services, graphics, memory, input, storage, and media. It incorporates attributed upstream foundations; it does not claim that every component was independently authored from inception.

IRX has progressed through local account selection, the animated title introduction, the Continue (A) prompt, and into the initial configuration interface. The next major development revision, R78, targets the remaining transition into the main menu, continuous movie audio, and improved runtime throughput. The longer-term objective is sustained, correct 60+ FPS in demanding titles on suitable hardware. That objective has not been demonstrated.

## Current status

### Vindex milestone: initial settings interface reached (R77)

> **Approaching the main menu.** Ira Rationis has advanced beyond the intro logo and Continue (A) screen into the title's initial configuration interface, including minimap and subtitle settings. R78 is our next push toward a stable main menu, with movie audio correction and major performance improvements developed alongside compatibility.

On November 26, 2026 (US Central time), the maintainer observed and navigated the initial settings interface during native guest execution. UI navigation audio appeared normal; movie soundtrack playback remained choppy, with intermittent silence, and sounded worse in the R77 run. This is the furthest observed startup progression to date. Main-menu execution and gameplay have not yet been demonstrated, and the number of remaining compatibility boundaries is unknown.

The R77 local Native AOT run on an NVIDIA GeForce RTX 4090 crossed the previous indexed UI blocker. Dithered, plain textured and alpha-mask UI variants report native GPU completion. The runtime subsequently terminated on a new non-indexed draw involving a three-vertex rectangle-list workload (guest topology 17), not the prior indexed UI shader.

| Latest runtime evidence (R77) | Observed |
| --- | ---: |
| Elapsed time at last startup observation | 74.008 seconds |
| Bound images / missing imports | 5 / 0 |
| Successful presentations / attempts | 832 / 832 |
| Draw calls observed | 7,823 |
| Compute dispatches observed | 782 |
| Last sampled presentation rate | 8.97/s |
| Prior intro milestone presentations (R73) | 616 |
| Main menu | Not yet reached |

The presentation rate is not a gameplay FPS benchmark. R77 progressed into a more demanding UI workload; its lower late presentation rate is a performance concern, not a controlled same-scene regression measurement. Draw and dispatch counters record observed calls, not a guarantee that each produced correct pixels. Visual progression and audio quality are maintainer observations; automated account/media lifecycle and pixel-content indicators remain unverified or not observed. No commercial title captures are distributed with the project.

### Startup progression

```mermaid
flowchart LR
    A[Native launch] --> B[Local account selection]
    B --> C[Intro logo animation]
    C --> D[Continue A prompt]
    D --> E[Initial settings UI]
    E --> F[Non-indexed draw blocked]
    F -. R78 target .-> G[Main menu]
    G -. future .-> H[Gameplay]
    C --> I[Movie audio: unresolved]
    E --> J[UI navigation audio observed]
    classDef reached fill:#d8eee0,stroke:#28704a,color:#163822
    classDef blocked fill:#f8e1d5,stroke:#a64e2b,color:#59250e
    class A,B,C,D,E,J reached
    class F,I blocked
```

### Component status

| Area | Evidence and current boundary |
| --- | --- |
| Native execution | Native entry, five bound images, and zero missing imports in the R77 run. Import resolution does not establish behavioral parity. |
| Graphics | Title intro, Continue prompt and initial settings UI observed. Planar-color, sRGB texture strips, and dithered/plain/alpha-mask indexed UI workloads report GPU completion. Texture arrays, typed views, dynamic sampler translation and additional texture formats are supported within validated contracts. General shader coverage, resource coherence and command semantics remain incomplete. |
| Compute | Existing 3D color transform and two-/three-segment curve LUT dispatches complete on the GPU. Authored 17- and 29-texel numerical readback fixtures pass for both device ABI paths at the existing tolerance. General RDNA2 translation and console-equivalent title output remain unverified. |
| Input and local account | Guest-consumed wired DualSense input, local profile selection and settings navigation observed; the real host async runtime successfully returns an offline local-user handle. General callback delivery, online identity, privilege and token services remain incomplete or explicitly unsupported. |
| Storage | Local strict storage contracts and native DirectStorage fixture passed, including raw/Zlib reads, virtual-drive access, physical-page writes and ordered error reporting. BCPack and shared weighted scheduling remain outstanding. |
| Title audio and media | Guest 48 kHz XApu/Opus and pre-skip contracts pass. Narration and UI navigation audio were reported to sound normal, whereas the movie soundtrack remains severely intermittent and distorted. Experimental mixer/movie scheduling did not resolve the defect. Bink PCM production, buffering, timing and WASAPI delivery require direct investigation. |
| Memory and performance | Guarded memory publication, ordered same-queue texture uploads, optimized detiling, bounded immutable layout caching and CLZERO batching reduce identified subsystem costs. The ordered-upload GPU readback fixture passes. R77 late presentation throughput remains below the requested rate; overall improvement has not been demonstrated. See [validation evidence](docs/DIAGNOSTICS.md#resident-page-validation-results). |
| Runtime services | Host initialization, local-user completion, gamertag reads and persistent storage access observed. Unsupported online/provider requests remain explicit failures. See [service contracts](docs/runtime-services.md). |
| Diagnostics and validation | R77 local Release build (zero warnings/errors), Native AOT publication, runtime contracts, graphics probes, native entry/thread, GameInput/physical memory and DirectStorage fixtures passed. Bounded captures remain local. GitHub Actions status must be verified for the exact commit. |
| Post-intro transition | Initial configuration UI reached; strict execution terminates at a new non-indexed raster workload. Main-menu transition is the R78 target. |
| Gameplay and sustained FPS | Not demonstrated. No overall completion percentage or competitive performance claim is made. |

### R78: the next major implementation

| Development front | Objective |
| --- | --- |
| **Main menu** | Resolve the captured non-indexed rectangle-list pipeline and subsequent compatibility boundaries. Validate correct resource bindings, fixed-function state and GPU output; establish stable menu rendering and input. |
| **Movie audio** | Trace actual Bink PCM producer/consumer timing, buffer occupancy, underruns and endpoint delivery. Correct the identified defect without regressing working narration and UI audio. |
| **Performance** | Profile comparable scenes, isolate CPU emulation, resource staging, submission and guest worker costs, and reduce the critical path toward sustained requested presentation cadence without weakening synchronization. |

R78 is a development objective, not a promised release or guarantee that the next patch will reach the main menu. The three fronts will be developed together, with consolidated validation and explicit unsupported-operation boundaries.

The R77 local validation logs establish the contracts above; HLSL compiler warnings remain. CI and release validation require separate evidence for the exact commit. See [build and validation](docs/RELEASING.md#local-build-and-component-checks). The [development handoff](docs/development-handoff.md) records an earlier checkpoint; this milestone and blocker supersede its historical status.

Historical measurements and their limits remain in the [Architecture Atlas](docs/ARCHITECTURE-ATLAS.md). Compare the same scene, settings, capture interval, and matching symbols; inclusive stack percentages are not FPS measurements.

## Engineering direction

- Implement runtime behavior in C# wherever possible. C++ is reserved for genuine platform requirements, such as the vectored exception boundary, and existing native probes.
- Make ownership, ordering, lifetime, and failures explicit. Use bounded workers for independent preparation and preserve guest-visible submission order.
- Use component folders and namespaces, one top-level type per file, and no authored nested types. Keep models with their owning component; create shared abstractions when actual consumers justify them.
- Prefer descriptive names, deliberate spacing, and self-documenting control flow. Comments explain constraints, contracts, and performance costs. Use `//TODO: Damon -> ...` for actionable follow-ups.
- Apply SOLID, DRY, YAGNI, and design patterns where they simplify real responsibilities. Reduce cyclomatic/Halstead complexity while improving maintainability; moving files alone does not establish a metric improvement.
- Base optimizations on traces and observed results. Document meaningful time/space complexity and preserve correctness.

Reference projects help explain contracts and identify questions. Replacing inherited implementation requires an independent design and retained contract tests; renaming or relocating adapted code does not change its provenance.

## Architecture

```mermaid
flowchart TD
    CLI[irx CLI] --> RUN[Native title launch]
    CLI --> PROBE[Authored media and graphics probes]
    CLI --> TRACE[Process-scoped network trace]
    RUN --> LOADER[PE loading, imports and TLS]
    LOADER --> EXEC[Native x86-64 execution]
    EXEC --> VEH[Native exception boundary]
    EXEC --> HLE[C# platform ABI]
    HLE --> MEMORY[Physical pages and mapped-view index]
    HLE --> INPUT[GameInput and controllers]
    HLE --> STORAGE[Ordered storage workers]
    HLE --> AUDIO[APU, ACP and PCM contracts]
    HLE --> GRAPHICS[C# graphics facade]
    GRAPHICS --> SHADERS[Shader translation and caches]
    GRAPHICS --> COMMANDS[Commands and resource lifetime]
    SHADERS --> HOST[Host D3D12 and DXGI]
    COMMANDS --> HOST
    PROBE --> MEDIA[Local media pipeline]
    MEDIA --> HOST
    MEDIA --> PCM[Host PCM output]
    MEDIA -. guest movie integration remains incomplete .-> AUDIO
```

Solid edges show implemented paths, not complete API coverage. The authored media sample uses an explicit opt-in contract; it is not a replacement for a title's movie or account services.

| Project | Responsibility |
| --- | --- |
| `IraRationis.Cli` | Commands, launch, startup/network diagnostics, and authored media integration |
| `IraRationis.Core` | Configuration, asset policy, binary utilities, bounded preparation, and retirement |
| `IraRationis.PE` / `IraRationis.Loader` | PE parsing/models, images, imports, TLS, and instruction compatibility |
| `IraRationis.HLE` | Input, storage, audio, graphics, kernel, memory, and runtime ABI services |
| `IraRationis.Graphics` / `IraRationis.Shaders` | Graphics ABI, commands, descriptors, resources, presentation, and shader translation |
| `IraRationis.Memory` | Isolated virtual unified memory and backing-strategy research |
| `IraRationis.Media` | Audio, video, decoding, playback, and presentation |
| `IraRationis.Game` / `IraRationis.Pipeline` | Build identification, archives, and conversion infrastructure |
| `IraRationis.Generators` | Compile-time ABI fallback generation |
| `IraRationis.Native` | Native exception boundary and independent native probes |
| `IraRationis.Runtime.Tests` / `tests` | Component contracts and authored CPU, GPU, media, and native fixtures |

`ConsoleGraphicsDevice` remains one partial public facade, organized into folders such as `ConsoleGraphicsDevice/Drawing/VertexFetchSourceProbe.cs`. Its generated ABI slots retain that type identity. Extracted types use component namespaces and individual files.

### CPU/GPU memory and concurrency

The guest must see coherent memory even when host RAM and VRAM hold different representations. Indexed physical views improve bookkeeping; they do not provide general CPU/GPU alias coherence. The isolated virtual page-table research also does not intercept arbitrary native CPU writes.

Host materialization currently has a fixed 512 MiB total budget and 256 MiB per-resource limit. The versioned buffer observes explicit writes, not arbitrary guest-pointer writes, physical aliases, or GPU-authored contents. Residency expansion, dirty-range transfers, and eviction must respect these ownership and completion boundaries.

Ordered/deferred preparation queues already provide bounded workers and ordered consumption. More cores should reduce measured independent work without introducing unbounded queues or reordering guest-visible operations. Higher VRAM allocation or worker count alone is not evidence of better performance.

## Build and validate

Use Windows x64, the SDK pinned in [global.json](global.json), Visual Studio C++ build tools for the existing native boundary, and a compatible D3D12 driver for GPU probes. FFmpeg is supplied separately for its media paths; Media Foundation requires installed Windows codecs.

From the repository root, stop at the first failed command:

```powershell
dotnet restore IraRationis.slnx
dotnet build IraRationis.slnx -c Release --no-restore
dotnet run --project IraRationis.Runtime.Tests -c Release --no-build
dotnet run --project tests/startup-evidence/StartupEvidence.Contracts.csproj -c Release --no-build
dotnet run --project tests/versioned-upload/VersionedUpload.Contracts.csproj -c Release --no-build
dotnet run --project tests/descriptor-bindings/DescriptorBindings.Contracts.csproj -c Release --no-build
dotnet run --project tests/storage-contract/StorageContract.csproj -c Release --no-build
dotnet run --project tests/runtime-media/RuntimeMedia.csproj -c Release --no-build
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native
.\artifacts\native\irx.exe --help
```

For the consolidated local validation and R77 launch profile, use the reviewed scripts from the repository root:

```powershell
.\scripts\Invoke-Validation.ps1 -OutputDirectory "artifacts/validation/R77" -UnblockReviewedFixtures
.\scripts\Invoke-R77.ps1
```

Only use `-UnblockReviewedFixtures` after reviewing the named native fixtures. The launch profile contains experimental performance and movie scheduling options; `-DisableMovieScheduling` and `-DisableClzeroBatch` permit comparison runs. These options do not establish correct title playback.

Console contract projects execute through `dotnet run`; building the solution does not run them. Preserve the exact published EXE/DLL/PDB set when profiling, and use a distinct directory for each build being compared.

Launch your authorized title through the picker or supply its executable:

```powershell
.\artifacts\native\irx.exe run
.\artifacts\native\irx.exe run 'D:\AuthorizedTitle\game.exe'
```

### Independent media and graphics

```powershell
$intro = (Resolve-Path .\tests\media-baseline\vindex-intro.mp4).Path
.\artifacts\native\irx.exe media-probe --mf $intro --play
.\artifacts\native\irx.exe media-probe --mf $intro --gpu

$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
& .\artifacts\native\irx.exe media-probe $ffmpeg $intro --stream-gpu

.\artifacts\native\irx.exe graphics-probe
.\artifacts\native\irx.exe veh-probe

.\artifacts\native\irx.exe instruction-probe
.\artifacts\native\irx.exe present-probe --seconds 5 --fps 60
.\artifacts\native\irx.exe memory-probe --self-test
```

These commands can open a window or play audio. `--stream` and `--stream-gpu` use FFmpeg; `--mf` supports batch inspection, `--play`, and `--gpu`. See [media validation](tests/media-baseline/README.md), [GPU contracts](tests/gpu-media/README.md), and [authored runtime integration](docs/RUNTIME-INTEGRATION.md) for fixture expectations and boundaries.

## Documentation

| Document | Purpose |
| --- | --- |
| [Architecture Atlas](docs/ARCHITECTURE-ATLAS.md) | Subsystem relationships, contracts, diagrams, and historical evidence |
| [Architecture and Research](docs/architecture.md) | Runtime and memory-research boundaries |
| [Local Runtime Services](docs/runtime-services.md) | Local user/async ownership, persistent storage, routing, and validation boundaries |
| [Architectural Divergence](docs/ARCHITECTURAL-DIVERGENCE.md) | Independent direction and engineering principles |
| [Runtime Integration](docs/RUNTIME-INTEGRATION.md) | Authored fixtures, utility execution, and integration limits |
| [Diagnostics](docs/DIAGNOSTICS.md) | Startup/input investigation, DNS/socket capture, JSONL export, and CPU profiling |
| [Implementation Provenance](docs/PROVENANCE.md) | Source ancestry, reference use, and remaining attribution work |
| [Quick Start](docs/QUICKSTART.txt) | Using a published Windows distribution |
| [Release Procedure](docs/RELEASING.md) | Validation, packaging, tags, and draft releases |

Documentation is organized by topic. Revision-specific handoffs belong in [Git history](https://github.com/DBWilliamEngineering/IraRationis/commits/main/) rather than competing documents. Historical log markers and commit references remain useful evidence.

## Distribution and releases

The [Windows x64 workflow](.github/workflows/build.yml) defines builds, explicit contract execution, Native AOT publication, native smoke fixtures, and headless media checks. Optional Media Foundation validation requires manual dispatch and available codecs. A local build does not establish CI status; inspect the run for the exact commit.

Successful runs publish a Windows ZIP and unpacked distribution. Keep `irx.exe`, `irx-native.dll`, and the other dependencies together. The package includes the authored Vindex fixture, documentation, and network-export script; it does not bundle FFmpeg or commercial title files. Distribution archives omit PDBs, so retain local publish output for profiling.

Successful main-branch commits with an `R<number>:` subject can receive an automatic revision tag. Separately, `v*` tag pushes gate a draft prerelease. An automatic revision tag is not a published release or proof of game compatibility. See [Release Procedure](docs/RELEASING.md).

| Version | Codename | State |
| --- | --- | --- |
| 0.1 | Vindex | Current development |
| 0.2 | Abaddon | Reserved |
| 0.3 | Apollyon | Reserved |
| 0.4 | Prometheus | Reserved |
| 1.0 | Invictus | Reserved |

Reserved names are not delivery commitments. Tags do not automatically change the assembly version.

## Licensing and attribution

The runtime incorporates adapted work from [Zettrox/ztrx](https://github.com/Zettrox/ztrx), based on VIctory and victory-fork; the recorded original reference tree is `90b529f878a470fec9bf07b7be316bc769a12232`. Source notices and the [provenance inventory](docs/PROVENANCE.md) distinguish inherited implementation, independently developed extensions, and references.

- Runtime software: [GPL-3.0-or-later](LICENSE).
- Separately designated independent materials: [proprietary material notice](LICENSE-PROPRIETARY.md), subject to its scope and applicable third-party rights.
- External dependencies retain their applicable licenses. FFmpeg is supplied separately.

No commercial title assets, executables, credentials, shader dumps, caches, or title-derived captures belong in version control. Native title execution is experimental and unsandboxed; use authorized, trusted executables and dependencies. IRX is not affiliated with or endorsed by Microsoft, Rockstar Games, or the referenced projects.
