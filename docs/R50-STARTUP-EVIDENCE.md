# R50 | Startup evidence and host first-frame synchronization

Base: `7f12083610f70b10a759b9698e204cdc29de34af` (R49).

Extract into the repository root; the archive contains complete source files, not a patch or generator. Existing SDK default compile globs pick up the new production source files. The standalone CPU contract project is run explicitly; no solution or dependency edits are needed. The existing README and R49 presenter are unchanged.

## Diagnostic experiment

`IRX_SAMPLE_GRAPHICS_LOGS=1` samples only the two known resource-view and command-list success messages during CLI runtime execution. It emits the first four and subsequent powers of two for each category; other messages pass through. `IRX_VERBOSE_GRAPHICS=1` overrides this option. A normal scope exit emits counts; fail-fast termination may not. This is an opt-in process-wide stderr wrapper, not a change to GPU state. It reduces actual output, not formatting or synchronized writer overhead; previously captured writers or non-string overloads may bypass sampling. Do not enable it when every creation address is needed.

The supplied 6,022-line log has 4,601 resource-view and 625 list creation lines. The sampling rule would retain 14 and 11, respectively, if all flowed through the wrapper. This is a predicted line-count reduction, not measured runtime improvement. Compare identical authorized test workloads with the option off/on. The earlier log predates the verified R49 source push; it is not evidence of R49 speed.

## Host media integration

`GpuMediaPlaybackSession` now submits the opening frame before starting the audio clock; an unaccepted opening frame fails explicitly without beginning audio. This is acceptance by the presenter, not verified monitor scanout. Subsequent frames follow the monotonic audio clock and preserve accounting for gaps, refusals, and early completion. Cancellation remains distinct from completion. This applies to existing `media-probe --gpu` and original sample playback, on either R49 queue configuration. No guest movie ABI, proprietary asset format, account handling, texture or shader compatibility is added. Initial presenter calls may block according to the existing GPU wait contract; the playback timeout begins when audio starts.

## Verify

```powershell
dotnet build IraRationis.slnx -c Release -p:Platform="Any CPU"
dotnet run --project tests/r50-contracts/R50.Contracts.csproj -c Release
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native-r50
$env:IRX_HOST_MEDIA_DIAGNOSTICS = '1'
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
$env:IRX_HOST_MEDIA_COPY_QUEUE = '0'
& .\artifacts\native-r50\irx.exe media-probe --mf $clip --gpu
$env:IRX_HOST_MEDIA_COPY_QUEUE = '1'
& .\artifacts\native-r50\irx.exe media-probe --mf $clip --gpu
Remove-Item Env:IRX_HOST_MEDIA_COPY_QUEUE
Remove-Item Env:IRX_HOST_MEDIA_DIAGNOSTICS
```

For the 60-frame fixture, compare acceptance of frame 0 before audio start, 60 presented/0 skipped, approximately two seconds of audio-clock progress, actual visible motion and sound, and R49 queue counters. The existing CLI does not fail solely on skipped frames; inspect the printed totals. Run the independent CPU contracts first. For authorized runtime workloads, compare `IRX_SAMPLE_GRAPHICS_LOGS=0` versus `1` with the same configuration. Remove the variable afterward.

## Remaining work and constraints

No Windows SDK, .NET SDK or GPU is available in the artifact creation environment. Compilation, contract test execution, Native AOT, live audiovisual correctness and performance are unverified. The next compatibility tasks are resource versions and descriptor identity, independently validated shader lowering, ordered submission with completion-based retirement, and a documented media/audio service contract with original fixtures. Do not report game logo/video/menu support on the basis of the host fixture alone.
