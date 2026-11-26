# R51: Versioned host buffer uploads

Baseline: `6a11bac6c31575b70c8ded8f0afbf69ef46ae03a` (R50.2 plus partial quality pass).

## Scope

- Explicitly written, bounded CPU-owned linear buffers. The initial capture covers the entire buffer; later captures contain a coalesced dirty interval. Identical writes and empty writes do not dirty the buffer.
- One immutable snapshot at a time. Writes after capture remain dirty; cancellation is permitted only before submission. Completion is published only after the caller proves GPU completion. Single-owner use is enforced.
- The existing `graphics-probe` invokes the versioned upload proof using the existing 4096-byte DEFAULT, UPLOAD and READBACK resources. It performs one full transfer and one 64-byte update, checking every destination byte after both. An unchanged write must schedule nothing. The probe uses the existing DIRECT queue and synchronous host completion helper; readback and waits are diagnostic only.
- Pitched-plane copy validates complete source and destination extents before touching data. The media presenter names plane extents explicitly. The regression test covers the R50.2 chroma-height failure and padding preservation.
- Cleanup is focused on touched graphics code, the existing frame timeline and fence retirement queue. No broad formatting-only rewrite.

## Validation commands (Windows, repository root)

```powershell
dotnet run --project tests/r51-contracts/R51.Contracts.csproj -c Release
dotnet run --project tests/r50-contracts/R50.Contracts.csproj -c Release
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native-r51
.\artifacts\native-r51\irx.exe graphics-probe
$env:IRX_HOST_MEDIA_DIAGNOSTICS = '1'
$env:IRX_HOST_MEDIA_COPY_QUEUE = '1'
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
.\artifacts\native-r51\irx.exe media-probe --mf $clip --gpu
```

Expected new markers (targets, not observed results):

```text
IRX-R51 CPU contracts passed.
[graphics-probe] IRX-R51: fullBytes=4096; partialBytes=64; uploads=2; unchangedUploads=0; completedVersion=3; readback=verified.
```

The R50 CPU contract and R50.2 media fixture should continue to pass. No benchmark or speedup is claimed. The patch has not been compiled or run on Windows in the authoring environment.

## Limits and next integration gate

The tracker owns its input bytes and does not observe native pointer writes, shared aliases, GPU writes, texture tiling, descriptor binding or guest resource lifetimes. The coalesced range may contain unchanged bytes between separate writes. It has one outstanding snapshot, not a multi-queue scheduler. Do not attach it to borrowed memory or claim general guest coherence. A later revision can connect a documented explicit-write producer and a real fence timeline to a bounded retirement queue after ownership and aliasing semantics are established. The original authored fixtures remain the validation boundary.
