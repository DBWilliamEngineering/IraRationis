# Runtime utility and authored media integration

This document describes current ownership contracts and authored diagnostic paths. It replaces release-specific patch instructions. A successful build does not establish game movie playback, account selection, audible output, or gameplay performance.

## Completion-owned utility contexts

Runtime texture-upload and compute paths can reuse a DIRECT command allocator/list, upload allocation, and optional shader-visible descriptor heap after a real host completion fence. Aborted recording is destroyed rather than returned to the idle pool.

The pool retains at most eight idle contexts and 64 MiB of logical idle upload capacity. Acquisition matches the host device, exact upload capacity, descriptor count, and versioned mode. Versioned contexts also match the creating managed thread. Active submissions retain the existing synchronous ordering; the idle budget does not cap all active GPU memory.

Versioned uploads compare explicitly captured CPU data in 256-byte blocks. Unchanged blocks require no new CPU write into the UPLOAD heap. This does not observe arbitrary guest-pointer writes or establish alias/GPU coherence. Texture staging still reads and detiles its source, and runtime texture descriptors are rewritten on acquisition. Submission ordering, resource transitions, and completion waits remain explicit.

Sources: [utility pool](../IraRationis.Graphics/ConsoleGraphicsDevice/Commands/UtilityExecution.cs), [utility context](../IraRationis.Graphics/Commands/UtilityContext.cs), and [versioned upload buffer](../IraRationis.Graphics/Resources/VersionedUploadBuffer.cs).

Set `IRX_RUNTIME_UTILITY_POOL=0` before launch to disable idle reuse for a controlled comparison. The existing `IRX-R53-UTILITY` marker reports cumulative creation, reuse, upload, and unchanged-upload counters. A missing marker means those paths were not observed; it is not a successful test result. Compare the same workload, settings, and capture interval with and without pooling.

The initial design notes record consultation of a public GPL Victory fork at commit `85d564167324056381327ff50f52da85048c84e3`, specifically `crates/victory-gpu/src/descriptor/host.rs` and `crates/victory-gpu/src/queue/behind.rs`. They describe independently implemented synchronous bounded utility contexts, with no Rust source copied in that change. Existing component notices remain applicable; see [implementation provenance](PROVENANCE.md).

## Authored runtime media service

`RuntimeMediaSample` explicitly registers `irx-sample-media.dll!IrxPlaySampleMedia` only when `IRX_SAMPLE_MEDIA_CLIP` selects an existing local MP4. The original [native sample](../tests/native/media-sample/probe.c) calls this ABI itself. It is not a console movie API, automatic game intro replacement, or account-service implementation.

The service accepts one invocation on the title-main thread. Without `IRX_SAMPLE_MEDIA_FFMPEG`, it batch-decodes through Media Foundation and exercises two playback lifetimes using a runtime-backed `MediaGpuContext`. With that variable set to a trusted FFmpeg executable, it uses bounded GPU audiovisual streaming instead. Both paths use the runtime device/queue integration and explicit presenter and fence lifetimes.

Optional authored checks apply only to the FFmpeg streaming branch:

| Setting | Behavior |
| --- | --- |
| `IRX_SAMPLE_SCALAR_BATCH=1` | Execute and read back the authored scalar workload before playback. |
| `IRX_SAMPLE_TYPED_COMPUTE=1` | Add the typed-compute fixture within the enabled scalar batch. |
| `IRX_SAMPLE_PROGRAMMABLE_RASTER=1` | Run the authored raster check before playback. |

Sources: [sample service](../IraRationis.Cli/Media/RuntimeMediaSample.cs), [scalar execution](../IraRationis.Graphics/ConsoleGraphicsDevice/Shaders/AuthoredScalarExecution.cs), and [media GPU context](../IraRationis.Graphics/Media/MediaGpuContext.cs).

## Scalar batch and resource lifetime

The scalar fixture compares CPU-reference results with the optimized scalar value graph, lowers supported operations to typed HLSL, and executes three dispatches with two descriptor tables bound in A/B/A order. Each dispatch clears its output and copies to a separate readback region, so an omitted dispatch cannot inherit an earlier result. Scalar state contains 103 words: 102 registers plus SCC. Three dispatches verify 309 words.

One utility upload allocation contains a 256-byte constant range, two 412-byte inputs, and a 412-byte zero range. Descriptor leases remain active throughout recording and retire only after the actual completion wait. Logical retirement ordinals are bookkeeping values, not native fence values. Output transitions through COMMON, COPY_DEST, UAV, COPY_SOURCE, and COMMON for each dispatch; readback remains COPY_DEST and upload remains GENERIC_READ.

The batch performs one submission and one completion wait. Its existing `IRX-R58-62` marker reports dispatch/table counts, upload bytes, lowering/compilation/recording/completion timings, and verified readback. Other authored checks retain their historical log markers. Those labels do not require applying an old patch.

This workload does not establish general guest shader coverage, arbitrary root signatures, texture alias coherence, cross-queue scheduling, or game performance. Fixed-rate audiovisual playback is also unsuitable for measuring maximum rendering throughput.

## Validation paths

Use the current [build and acceptance commands](RELEASING.md). The startup-evidence, versioned-upload, and descriptor-binding projects check CPU-side contracts. `irx graphics-probe` exercises GPU contracts on suitable hardware. Independent media tests are documented beside [media primitives](../tests/media-baseline/README.md), [runtime media](../tests/runtime-media/README.md), and [GPU conversion](../tests/gpu-media/README.md).

For the original native sample, compile its import library and fixture from an x64 Visual Studio Developer PowerShell, after publishing the runtime to `artifacts/native`:

```powershell
$sample = (Resolve-Path tests/native/media-sample).Path
$stage = (New-Item -ItemType Directory -Force artifacts/runtime-media-sample).FullName
$irx = (Resolve-Path artifacts/native/irx.exe).Path
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source

Push-Location $stage
try {
    & lib.exe /nologo /machine:x64 "/def:$sample\sample-media.def" /out:sample-media.lib
    if ($LASTEXITCODE -ne 0) { throw 'Sample import library failed' }
    & cl.exe /nologo /W4 /WX /O2 /GS- /Zl "$sample\probe.c" /link /NODEFAULTLIB /ENTRY:ProbeEntry /SUBSYSTEM:CONSOLE /BASE:0x600000000 /DYNAMICBASE /FIXED:NO /OUT:probe.exe sample-media.lib kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Sample fixture compilation failed' }
}
finally {
    Pop-Location
}

$env:IRX_SAMPLE_MEDIA_CLIP = $clip
$env:IRX_SAMPLE_MEDIA_FFMPEG = $ffmpeg
$env:IRX_SAMPLE_SCALAR_BATCH = '1'
try {
    & $irx run (Join-Path $stage 'probe.exe')
    if ($LASTEXITCODE -ne 42) { throw "Unexpected authored sample exit: $LASTEXITCODE" }
}
finally {
    Remove-Item Env:IRX_SAMPLE_MEDIA_CLIP -ErrorAction SilentlyContinue
    Remove-Item Env:IRX_SAMPLE_MEDIA_FFMPEG -ErrorAction SilentlyContinue
    Remove-Item Env:IRX_SAMPLE_SCALAR_BATCH -ErrorAction SilentlyContinue
}
```

Keep the playback window visible and inspect readback and presented/skipped-frame evidence. The native sample returns 42 when its explicitly requested service succeeds, or 51 when that service reports failure. Neither sentinel is a commercial game startup result. No former patch-application script is required.
