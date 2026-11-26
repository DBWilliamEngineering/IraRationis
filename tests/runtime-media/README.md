# R40 runtime media contracts

This is a standalone synthetic test of the runtime library interfaces, not a commercial game or a guest startup workaround. The XApu hook remains explicitly host-registered for one connection/voice; it does not activate automatically during a normal launch. The NV12 upload type prepares CPU snapshots and pitched plane copies. It does not create D3D12 resources, submit commands, or establish GPU coherency.

From the repository root, after extracting this archive over R39.1:

```powershell
dotnet build IraRationis.slnx -c Release
dotnet run --project tests/runtime-media/RuntimeMedia.csproj -c Release
dotnet run --project tests/runtime-media/RuntimeMedia.csproj -c Release -- --play
```

The first test invocation is headless. The second invokes the Windows audio device for an original synthetic Opus packet, repeatedly decoded through the XApu ABI. A repeated packet is a diagnostic sound, not a final mixed stream. Expect a nonzero peak and one packet / 960 frames headless; 50 packets / 48000 frames with playback enabled. The device monitor may drop queued packets under backpressure; its counters distinguish accepted, completed and dropped packets. No network or commercial media is used.

Existing source baseline: main tree `44ad3c30b48ca04e185fd52a2894e2dbd0be9829`, XApu blob `1916daaaa00bf96336a28d1ece33467d28112b75`. Review local changes before overwriting. The validated R39.1 decoder is not modified. The test project is separate from the solution and must be run explicitly; CI wiring is not included in this patch. Windows compile and execution must be verified on the target machine.
