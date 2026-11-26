# R49: Host media copy queue

Baseline: `52d2ceebd751f0b69b64e2053435fb5cd84cf76e`.
Original presenter Git blob: `74833d490988497efe1ffce17a88805fe01a6c6a`.

This is an opt-in host-authored media pipeline implementation, not a guest movie ABI or an account-service change. No guest texture, descriptor or shader semantics are changed. The existing single-queue mode remains the default. R48 diagnostics and PCM clock behavior remain intact. The R48 deferred preparation and retirement APIs are not connected in this change: persistent per-slot resources already have a bounded lifetime and reuse fence.

## Design

- A presenter owns one COPY queue and a dedicated copy fence on the same device as its existing DIRECT queue. Each of its three slots owns a copy allocator/list in addition to its existing resources.
- Wait for the slot's prior DIRECT completion before modifying mapped upload memory or resetting either allocator. Record and close both command lists before submitting either.
- Submit two plane copies on COPY. NV12 starts in COMMON, is implicitly promoted to COPY_DEST, and decays to COMMON at completion of the copy submission.
- Signal the copy fence. Enqueue a GPU-side DIRECT queue wait for that fence value, then submit the conversion and back-buffer copy. Return the texture to COMMON for the next use on COPY. The two queues have independent fence objects; the frame sequence is used as the monotonic value on each.
- The existing DIRECT fence is signaled after presentation and covers the preceding COPY dependency. Waiting for it before slot reuse or teardown protects both command lists and their resources. Failure to establish synchronization after submission is fatal, rather than permitting unsafe reuse.
- Retains the existing three-slot limit and zero video readback. This adds a queue, fence and three allocator/list pairs; it does not create a CPU worker pool or a general-purpose guest transfer scheduler.

Reference: [Microsoft multi-engine synchronization](https://learn.microsoft.com/en-us/windows/win32/direct3d12/user-mode-heap-synchronization), [resource barriers](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12). VIctory-7900XTX's documented dedicated transfer queue inspired the design; no reference code copied.

## Build and existing authorized media workflow

```powershell
dotnet build IraRationis.slnx -c Release -p:Platform="Any CPU"
dotnet publish IraRationis.Cli -c Release -r win-x64 -o artifacts/native-r49
$env:IRX_HOST_MEDIA_COPY_QUEUE = '1'
$env:IRX_HOST_MEDIA_DIAGNOSTICS = '1'
$clip = (Resolve-Path tests/media-baseline/vindex-intro.mp4).Path
& .\artifacts\native-r49\irx.exe media-probe --mf $clip --gpu
Remove-Item Env:IRX_HOST_MEDIA_COPY_QUEUE
Remove-Item Env:IRX_HOST_MEDIA_DIAGNOSTICS
```

For the existing 60-frame 320x180 clip, a successful full run should report 60 copy submissions, 60 GPU queue waits, 5,184,000 active pixel bytes and zero dedicated CPU copy-fence waits. The DIRECT slot-reuse wait may still block; copyCpuWaits=0 is a property of the submission path, not a measurement of all CPU waits. Compare R43 metrics and the actual visible/audible result; queue submission timings are CPU wall times, not GPU engine overlap measurements. The extra queue can cost more than it saves for small clips. No speedup is claimed. No new test executable is introduced.

## Validation scope

Source baseline hash checked, patch inspected, archive integrity checked. Windows compilation, D3D12 debug-layer validation, GPU execution and audiovisual verification remain outstanding. The copy queue is opt-in until validated. No game-specific changes, account bypass, or changes to proprietary asset formats.
