# R44: native sample-driven host media integration

Base: R41/R42 committed launcher (Git blob `3ab5e2e59e8a97c1f6e04d951629185c88beb713`), plus the R43 persistent compositor drop-in. Install R43 before this archive. The R43 package supplies `Nv12Video.cs`, `GpuMediaPlaybackSession.cs`, and `WindowsVideoWindow.Handle.cs`, which are prerequisites and are not replaced here.

R44 adds a host-owned, thread-affine media device and bounded cache of up to eight pipeline variants (keyed by the specialized shader source including output pitch). Each presenter retains independent COM references, its own swapchain, triple-buffered frame resources and fence. Normal disposal drains the existing R43 fence timeline before releasing those resources. The cache owns separate references. The existing `media-probe --gpu` path keeps its prior behavior when no shared context is supplied.

The authored native sample imports `IrxPlaySampleMedia` from `irx-sample-media.dll`. `RunCommand` registers this explicit sample-only HLE module only when the host operator selects a clip with `IRX_SAMPLE_MEDIA_CLIP`. The native sample requests the service once from the title-main thread. The callback decodes the host-selected local MP4 once and plays it twice, recreating the windows and frame resources but reusing the device, queue and pipeline. No guest pointers, arbitrary guest filenames, automatic intro injection, console movie ABI emulation, or guest graphics resource publication are implemented. The API is a test contract, not a security sandbox. Run only trusted authored fixtures.

## Windows acceptance

From the repository root:

```powershell
dotnet build IraRationis.slnx -c Release -p:Platform="Any CPU"
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native-r44
dotnet msbuild tests/native/media-sample/MediaSample.proj -t:Build '-p:VisualStudioRoot=P:\Program Files\Microsoft Visual Studio\18\Professional'
```

The last argument can be omitted when vswhere or VSINSTALLDIR resolves the installed x64 toolchain. The build target writes `artifacts/runtime-media-r44/probe.exe`.

```powershell
$env:IRX_SAMPLE_MEDIA_CLIP = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
try {
    & .\artifacts\native-r44\irx.exe run .\artifacts\runtime-media-r44\probe.exe --bind-only
    if ($LASTEXITCODE -ne 0) { throw 'Binding failed.' }
    & .\artifacts\native-r44\irx.exe run .\artifacts\runtime-media-r44\probe.exe
    if ($LASTEXITCODE -ne 42) { throw "Sample failed: $LASTEXITCODE" }
}
finally {
    Remove-Item Env:IRX_SAMPLE_MEDIA_CLIP -ErrorAction SilentlyContinue
}
```

The bind-only command must not decode or display video. The execution command should show two complete audible clips in succession. Expected markers (not measured):

- `IRX-R44: pass=1; presented=60; skipped=0; cancelled=False; audioSeconds=2.000; sourceFrames=60.`
- `IRX-R44: pass=2; presented=60; skipped=0; cancelled=False; audioSeconds=2.000; sourceFrames=60.`
- `IRX-R44: deviceCreations=1; queueCreations=1; presenterAcquisitions=2; pipelineMisses=1; pipelineHits=1.`
- `Title entry returned 42 (0x0000002A).`

The native sample returns 51 when the service reports a failure. Each pass must have no skipped frames for the sample to accept it. A cancelled window is failure, not a successful guest return. Playback relies on the R43 bounded local decoder and audio clock. The media asset is selected by the host environment, not read by the native fixture through a guest filesystem call.

The build and GPU test require Windows/MSVC/.NET 11 and a compatible GPU/audio device. This archive has not been compiled or executed on Windows. CI is unchanged because this interactive sample requires real display and audio. The next general architecture work is ownership/coherence for guest graphics resources, asynchronous uploads, and deferred command preparation. Those are not implemented by this sample bridge.
