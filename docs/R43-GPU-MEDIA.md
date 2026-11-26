# R43: Persistent host GPU media compositor

Baseline: `5fe0b5c7db911712958c3d9896c3c264899fc370` (R42).

This is host-side playback of explicitly supplied local, authorized media. It is not a guest movie decoder, a guest graphics resource publication mechanism, a game startup sequence replacement, or a change to `RunCommand`.

## Build and run

From the repository root in the normal x64 build environment:

```powershell
dotnet build IraRationis.slnx -c Release -p:Platform="Any CPU"
dotnet run --project tests/gpu-media/GpuMedia.csproj -c Release --no-build
dotnet publish IraRationis.Cli/IraRationis.Cli.csproj -c Release -r win-x64 -o artifacts/native-r43
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
& .\artifacts\native-r43\irx.exe media-probe --mf $clip --gpu
```

`--gpu` skips the CPU image inspection path. Existing `--play` and `--stream` behavior is preserved. A GPU failure is not silently replaced with software output.

## Acceptance

- One host device, queue, compute pipeline, and three reusable slots per session.
- Visible Vindex clip in a DXGI flip-discard swapchain window, with audible PCM.
- Expect 60 source frames, ideally 60 presented and zero skipped, about 2 seconds of audio. A missed deadline is logged as a skip; the result is not proof of display scanout.
- `submitted` must equal the final signaled fence value. `readbackBytes=0`.
- Report `blockingWaits`, `waitMs` and `meanSubmitWallMs` including vsync and slot waits. The wall metric is not GPU timestamp execution duration.
- Close window or Esc during playback; cancellation should return code 2 and release resources after the final fence.
- Run R42's regression again, including `--clip`, to ensure the unchanged conversion math is still correct.

## Ownership and performance

An explicit window handle is passed to a thread-affine presenter. The device, root signature and PSO persist. Each back-buffer index has its own upload allocation, NV12 resource, shader-visible SRVs, output buffer, allocator and list. Both input planes and output color pitch come from driver footprints. The existing R42 shader's addressing is specialized once to the actual output pitch. After dispatch, copy the pitched BGRA GPU buffer into the matching back buffer, then restore all resource states for reuse. Present and signal on the same queue. Wait before reusing a slot or releasing resources; fail fast if completion cannot be proven. This avoids a CPU readback, but does retain one GPU copy and CPU input upload. No separate transfer queue or GPU timer query is implemented in this revision.

The public architecture notes in avatarmanz/VIctory-7900XTX (GPL-3.0-or-later) describe dedicated texture upload queues and publication after upload completion. Those are useful design principles; this patch independently uses a conservative single-queue approach, not a port of that implementation. Benchmark before moving to a separate transfer queue or claiming a performance advantage.

## Limits

The bounded clip path remains 30 FPS, stereo 48 kHz PCM, maximum 5 seconds, at most 150 frames, up to 1920x1080 and 128 MiB of decoded NV12. The compositor accepts up to 3840x2160 per frame, but 4K/60 playback and throughput have not been tested. A separate host device and explicitly owned window are used; existing guest presentation remains unchanged. DXGI stretches the swapchain to the window, so aspect-preserving resize is not implemented. Windows compile and hardware execution must be verified locally.
