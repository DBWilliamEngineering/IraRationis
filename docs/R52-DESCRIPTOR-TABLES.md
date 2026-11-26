# R52: Bounded descriptor binding and GPU table validation

Baseline: `15e3184637eba902671f54421f12445d5bb2b81d`.

## Scope

R52 introduces an owner-thread, generation-checked catalog for explicitly registered
host buffers and a bounded cache for contiguous CBV/SRV/UAV tables. Its first
consumer is an independent authored D3D12 graphics probe, invoked through the
existing `graphics-probe` command for both device ABI variants. It does not
reinterpret existing guest descriptors, modify runtime submission or media paths,
or claim arbitrary guest memory or texture coherence.

### Catalog and cache contracts

- A resource identity combines a caller-supplied logical ID with a unique generation.
- Validate CBV alignment, 64 KiB limit, typed R32_UINT SRV/UAV ranges, UAV permission,
  and disallow simultaneous reads and writes of the same registered resource.
- The catalog does not detect physical aliases between distinct resource identities.
- An active lease pins each distinct referenced resource. Submission transfers that
  pin to the fence timeline; cancellation or proven completion releases it.
- Removal refuses resources with outstanding pins. The caller still owns the actual
  COM resource and must not release it independently of catalog lifetime.
- Each cache has a bounded number of three-descriptor slots. Cache hits reuse immutable
  descriptors; misses may replace only an idle slot with no outstanding GPU work.
- A materialization must be explicitly completed before submission. A cancelled,
  incomplete table must be rewritten before reuse.
- Fence values must be strictly increasing per submission on one cache timeline.
  Device-removed sentinel and regressing or unsubmitted completion values are rejected.
- This intentionally conservative implementation never shares an in-flight table,
  even if the bindings match. Backpressure is explicit. Cache lookup is O(capacity),
  not yet optimized for large heaps; leases allocate managed objects.

## GPU fixture

A standalone authored compute shader reads a uint from b0 and four uint elements
from t0, adds the constant to each element, then writes the result to u0. All three
views are in a single shader-visible descriptor table. Four serial submissions use
bindings A, A, B, A, with expected results [9,11,13,15], [9,11,13,15],
[29,39,49,59], and [9,11,13,15]. Before each dispatch a GPU copy clears the output,
so skipping a dispatch cannot pass by reusing its prior contents. Every dispatch is
followed by full readback and actual fence completion. Cache completion ordinals
are private serial probe values, not native fence values. The probe fails fast if
submission occurs but GPU completion cannot be established.

The probe owns all resources and uses an UPLOAD heap for constants and inputs.
It does not yet integrate R51's versioned DEFAULT-buffer upload path; R51 remains
separately exercised by the existing graphics probe. The descriptor cache does not
create native COM references or automatically retire actual native resources.

## Validation (Windows x64, repository root)

```powershell
dotnet run --project tests/r52-contracts/R52.Contracts.csproj -c Release
dotnet run --project tests/r51-contracts/R51.Contracts.csproj -c Release
dotnet run --project tests/r50-contracts/R50.Contracts.csproj -c Release
dotnet build IraRationis.slnx -c Release
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native-r52
.\artifacts\native-r52\irx.exe graphics-probe
$env:IRX_HOST_MEDIA_DIAGNOSTICS = '1'
$env:IRX_HOST_MEDIA_COPY_QUEUE = '1'
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
.\artifacts\native-r52\irx.exe media-probe --mf $clip --gpu
```

Expected CPU marker: `IRX-R52 CPU contracts passed.`

Expected graphics marker (twice, one per ABI):

```
[graphics-probe] IRX-R52: dispatches=4; tableHits=2; tableMisses=2; materializations=2; nativeDescriptors=6; heapCount=1; readback=verified.
```

R50/R51 markers should remain unchanged; media should present 60/60 with no skipped
frames. This patch was authored and application-checked in a Linux environment
without .NET or D3D12; C# build, analyzers and GPU execution remain unverified.
