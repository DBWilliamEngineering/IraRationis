# R42 | GPU NV12 interoperability milestone

Baseline: committed R41 tree `2df2646f19b18653e970f9a6aab1fabb842ea88c` (GitHub tree snapshot). R39.1 decoding, R40 CPU plane validation, and R41 executable/asset audio integration are preserved. Extract all files at the repository root; there is no patch application script.

## Architecture

`ConsoleGraphicsDevice.ConvertNv12FrameOnGpu` is a synchronous host-only diagnostic API. It creates a separate default-adapter D3D12 device and direct queue, creates one DEFAULT heap NV12 texture, gets two native copy footprints, fills a bounded UPLOAD heap with independent Y and UV rows, copies the two subresources, transitions to NON_PIXEL_SHADER_RESOURCE, and creates R8_UNORM and R8G8_UNORM SRVs of planes 0 and 1. An original HLSL compute shader uses the same integer BT.709 limited-range coefficients as the existing CPU reference. It stores packed BGRA to a DEFAULT heap raw UAV, transitions that buffer to COPY_SOURCE, copies into READBACK memory and waits for a dedicated fence. No CPU color conversion is performed in the GPU method. The regression test compares all four channels byte for byte against the CPU reference.

Resource ownership is local to a conversion. No resource is released while submitted work is in flight. A lost completion proof (signal error, removed device or 15-second fence timeout) terminates this diagnostic process, consistent with the existing host fence behavior. The allocation cap, stride validation, non-overlapping footprints and exact output extents are checked. No guest resources or state are modified.

The API is deliberately not a real-time player. It recreates device/queue/resources and compiles the shader for each call. The GPU output is read back to CPU, not composed into a swapchain. Pooled resources, asynchronous frame publication, persistent converter lifetime, device selection and swapchain integration are later work. It does not decode guest movie formats or implement commercial game startup. Test only authorized media and executables.

## Local GPU acceptance (Windows x64)

From the repository root:

```powershell
dotnet build IraRationis.slnx -c Release
dotnet run --project tests/gpu-media/GpuMedia.csproj -c Release --no-build
```

Optional: additionally decode and compare the first, middle and last frames of the existing authorized Vindex fixture using Media Foundation:

```powershell
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
dotnet run --project tests/gpu-media/GpuMedia.csproj -c Release --no-build -- --clip $clip
```

Expected output (not measured in the authoring environment): three synthetic cases, each with `planes=2`, `fence=1`, `mismatches=0`, `maxError=0`, followed by `IRX-R42 GPU media contracts passed.` The optional clip adds three frame comparisons. An unsupported adapter or conversion failure must fail the test rather than silently fall back to CPU rendering. The GPU test is compiled in CI but intentionally not run on a standard hosted runner without guaranteed D3D12 hardware.

## Automated native fixture compilation

The R41 native fixture can now be compiled without invoking `lib.exe` and `cl.exe` manually. The standalone MSBuild project locates the installed Visual Studio x64 toolchain using `VSINSTALLDIR` or `vswhere`, calls `vcvars64.bat` in the same command environment, then builds the original fixture and copies the external synthetic audio packet. It writes `artifacts/runtime-audio-r41/probe.exe` and `tone.opuspacket`.

```powershell
dotnet msbuild tests/native/runtime-audio/RuntimeAudio.proj -t:Build
```

Override auto-discovery if needed:

```powershell
dotnet msbuild tests/native/runtime-audio/RuntimeAudio.proj -t:Build '-p:VisualStudioRoot=P:\Program Files\Microsoft Visual Studio\18\Professional'
```

CI now builds this fixture and executes it through the runtime with the diagnostic PCM observer disabled. It also runs the R40 managed audio/video contract test headlessly. Existing tests, Native AOT distribution and release packaging remain in place. No GPU or speaker availability is assumed by the added CI gates.

## Validation limits

The authoring environment has no Windows D3D12 runtime or .NET compiler. Source/API checks, independent conversion and footprint models, and archive/XML/YAML checks are not a substitute for compilation or GPU acceptance. Do not mark GPU presentation, guest integration or audio mixing as complete on the basis of these tests.
