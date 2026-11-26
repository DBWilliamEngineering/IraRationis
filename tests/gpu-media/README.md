# GPU NV12 conversion contracts

`ConsoleGraphicsDevice.ConvertNv12FrameOnGpu` is a synchronous diagnostic owning a separate D3D12 device and direct queue. It uploads independent Y and UV rows into a two-plane NV12 texture, creates plane SRVs, converts to packed BGRA with an authored compute shader, copies into readback memory, and waits for completion. The tests compare every channel against the CPU reference.

The method checks strides, bounds, native layouts, copy footprints, and output extents. It does not modify guest resources or publish to a swapchain. Resources are not released while submitted work may remain in flight; losing completion proof terminates the diagnostic process under the existing fail-fast rule.

This per-conversion diagnostic is separate from the reusable presenter used by `media-probe --gpu` and `--stream-gpu`. Passing it establishes fixture conversion correctness, not game integration or general rendering throughput.

## Local acceptance

On Windows x64 with D3D12 hardware, after building the solution:

```powershell
dotnet run --project tests/gpu-media/GpuMedia.csproj -c Release --no-build
```

The synthetic cases cover 2x2, 320x180, and 514x34 images with padded source rows. Expect `mismatches=0` and `maxError=0`, followed by the existing `IRX-R42 GPU media contracts passed.` marker. Unsupported hardware or conversion failures must fail rather than silently use CPU rendering.

Optionally decode and compare the first, middle, and last frames of the original Vindex fixture through Media Foundation:

```powershell
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
dotnet run --project tests/gpu-media/GpuMedia.csproj -c Release --no-build -- --clip $clip
```

The project is compiled with the solution. CI does not run GPU acceptance on a standard hosted runner without guaranteed D3D12 hardware. A build result alone is not GPU execution evidence.

Native audio fixture compilation is documented in its own [acceptance guide](../native/runtime-audio/README.md). General build and media commands are in [release engineering](../../docs/RELEASING.md).
