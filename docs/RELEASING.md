# Build, validation, and release engineering

The Windows Native AOT workflow uses the SDK in `global.json` and the x64 MSVC tools for the existing native bridge and synthetic fixtures. The maintainer reported a successful local test build on November 26, 2026. That report does not establish a new CI result, GPU acceptance, or integrated game compatibility.

## Local build and component checks

Run from the repository root. Explicitly selecting `Any CPU` prevents an inherited MSVC `Platform=x64` environment from selecting a nonexistent solution configuration; publication still targets `win-x64`.

```powershell
dotnet restore IraRationis.slnx -p:Configuration=Release -p:Platform="Any CPU"
dotnet build IraRationis.slnx -c Release --no-restore -p:Platform="Any CPU"
dotnet run --project IraRationis.Runtime.Tests -c Release --no-build
dotnet run --project tests/startup-evidence/StartupEvidence.Contracts.csproj -c Release --no-build
dotnet run --project tests/versioned-upload/VersionedUpload.Contracts.csproj -c Release --no-build
dotnet run --project tests/descriptor-bindings/DescriptorBindings.Contracts.csproj -c Release --no-build
dotnet run --project tests/storage-contract/StorageContract.csproj -c Release --no-build
dotnet run --project tests/runtime-media/RuntimeMedia.csproj -c Release --no-build
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native
```

Check each exit status and stop on failure. Use a separate publish directory when retaining a build's exact EXE/DLL/PDB identity for profiling.

```powershell
$irx = (Resolve-Path artifacts/native/irx.exe).Path
& $irx memory-probe --self-test
& $irx veh-probe
python tests/smoke.py $irx
& ./tests/native/smoke.ps1 $irx
& ./tests/native/input-memory.ps1 $irx
& ./tests/native/storage.ps1 $irx
dotnet msbuild tests/native/runtime-audio/RuntimeAudio.proj -t:Build
```

The native probe scripts convert their expected synthetic success sentinel into exit code zero. For native audio launch and optional speaker acceptance, follow its [acceptance instructions](../tests/native/runtime-audio/README.md).

## Media and GPU acceptance

Select a trusted FFmpeg executable for independent synthetic decoding:

```powershell
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
$fixture = (Resolve-Path tests/media-baseline/fixture.mp4).Path
$intro = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path

dotnet run --project tests/media-baseline/MediaBaseline.csproj -c Release --no-build -- $ffmpeg $fixture
& $irx media-probe $ffmpeg $intro --stream
& $irx media-probe $ffmpeg $intro --stream-gpu
& $irx media-probe --mf $intro --play
& $irx media-probe --mf $intro --gpu
```

`--stream` and `--stream-gpu` require FFmpeg. `--mf` uses installed Media Foundation codecs and supports batch playback with `--play` or `--gpu`. Review visible output, sound, presented/skipped counts, and termination. The original Vindex fixture has 60 video frames over approximately two seconds; decoder padding can affect audio duration. Independent playback does not establish guest movie or account-service integration.

On Windows with D3D12 hardware, also run:

```powershell
& $irx graphics-probe
dotnet run --project tests/gpu-media/GpuMedia.csproj -c Release --no-build
dotnet run --project tests/gpu-media/GpuMedia.csproj -c Release --no-build -- --clip $intro
```

See [runtime integration](RUNTIME-INTEGRATION.md) for the authored native sample ABI and utility contexts, and [diagnostics](DIAGNOSTICS.md) for game-run observation and network capture.

## Continuous integration

The [workflow](../.github/workflows/build.yml) builds the solution, runs component and contract tests, publishes Native AOT, checks native entry/input/storage/audio fixtures, and performs noninteractive FFmpeg decoding. Its optional `test_native_media` workflow-dispatch input enables a headless Media Foundation check on a runner with suitable codecs.

GPU presentation, physical controller behavior, audible output, and guest startup remain local acceptance work. Compiling a GPU test does not execute it. Inspect the first failing step and its complete log; do not suppress failures to produce an artifact.

## Distribution and symbols

Successful validation uploads `irx-win-x64.zip` and the unpacked distribution. Extract the entire package and keep `irx-native.dll` beside `irx.exe`. The self-contained package includes the original Vindex clip, quick-start instructions, documentation, and license files. FFmpeg and commercial game assets are not bundled.

Distribution excludes PDB files. Local publication retains the matching NativeAOT and native-bridge symbols. Preserve them with the exact binaries used for a trace; a later build's PDB is not interchangeable.

## Tags and draft releases

The current workflow has two separate mechanisms:

- A successful push to `main` whose subject begins with an `R` revision followed by a colon can create an annotated revision tag. This existing `auto-tag` job has `contents: write` permission.
- A pushed `v*` tag triggers validation and then `draft-release`, which creates a draft prerelease with the binary ZIP, matching tracked-source archive, and SHA-256 manifest. This job also has `contents: write`. An automatically created revision tag is not a `v*` tag and does not itself request this release flow.

The normal validation job is read-only. Tags do not change the assembly version automatically.

Before a version-tag release:

1. Review the intended commit and its validation results. Confirm tracked files exclude game data, credentials, private traces, caches, and generated build output.
2. Retain applicable licenses and attribution, and review dependency notices and corresponding-source requirements.
3. Select a new `v*` tag, such as `v0.1.0`, for the validated source. Inspect the Actions result for that exact tagged commit.
4. Download the archives, verify checksums and contents, and review the draft notes before publishing manually.

The draft-release job does not publish automatically or overwrite an existing release. Inspect an existing draft if a rerun fails because its tag already has one. Successful packaging is separate evidence from working game startup, graphics correctness, or performance.
