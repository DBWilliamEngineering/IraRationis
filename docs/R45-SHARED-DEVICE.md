# R45: runtime-backed media device

Based on committed R44 `371b67561b1345cb1bfe773b71a71f065dadbc14`.

The authored sample uses the same process-owned D3D12 device as the existing runtime graphics device. A separate direct queue, presenter fences and COM references remain explicit. The existing standalone path still defaults to an isolated device. The shared device remains process-owned; context disposal does not destroy it.

This change does not implement a private movie ABI, guest texture publication, cross-queue synchronization, or commercial title compatibility. It does not change the default launch path for applications that do not explicitly use the sample service.

Build with `dotnet build IraRationis.slnx -c Release -p:Platform="Any CPU"`. Re-run the existing R44 authorized sample if desired. Expected R45 summary: `runtimeBacked=True; contextDeviceCreations=0; queueCreations=1; presenterAcquisitions=2; pipelineMisses=1; pipelineHits=1`. A runtime device creation can still occur, and is logged by the existing runtime initialization.

Windows build and execution are not verified here.
