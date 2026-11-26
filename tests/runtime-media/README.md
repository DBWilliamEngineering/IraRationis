# Runtime media contracts

This synthetic test covers PCM sink registration and NV12 upload snapshots through the runtime library interfaces. The XApu hook is explicitly registered for one connection/voice; ordinary launches do not activate it automatically. The NV12 type owns CPU snapshots and performs pitched plane copies; that contract alone does not create D3D12 resources or establish GPU coherence.

From the repository root after building the solution:

```powershell
dotnet run --project tests/runtime-media/RuntimeMedia.csproj -c Release --no-build
dotnet run --project tests/runtime-media/RuntimeMedia.csproj -c Release --no-build -- --play
```

The first invocation is headless. The second opens the Windows audio device and repeatedly decodes an original synthetic Opus packet through the XApu ABI. Expected observations are one packet and 960 frames headlessly, or 50 packets and 48,000 frames with playback enabled, with a nonzero peak. The repeated packet is diagnostic sound, not a mixed game stream. Under backpressure the monitor can drop packets; counters distinguish accepted, completed, and dropped work.

The project is included in `IraRationis.slnx`, and CI invokes the headless test. Audio-device acceptance remains local. No network or commercial media is used. Historical `IRX-R40` markers identify existing log events rather than a required patch baseline.

See [native runtime audio acceptance](../native/runtime-audio/README.md) for the separate PE/loader fixture and [runtime integration](../../docs/RUNTIME-INTEGRATION.md) for the authored audiovisual service.
