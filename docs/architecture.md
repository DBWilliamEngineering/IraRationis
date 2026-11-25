# IRX architecture and research

Vindex / First Light. Source inventory reviewed 2026-11-25. These are conceptual diagrams; implemented source is not equivalent to tested behavior. Research extensions are IRX-specific relative to the referenced upstream, not claims of worldwide novelty.

## Virtual unified memory (isolated research)

```mermaid
flowchart TD
    A[Guest virtual address] --> B[VirtualPageTable - 64 KiB software pages]
    B --> C[Host CPU virtual address]
    B --> D[Host GPU virtual address]
    B --> E[Residency strategy metadata]
    E -.-> F[Future adaptive residency and coherence]
    classDef research fill:#35171b,stroke:#b52837,color:#fff
    class B,E research
```

[VirtualPageTable](../IraRationis.Memory/VirtualPageTable.cs) supports map, translate and unmap. It does not modify hardware page tables or transparently intercept native accesses. It remains separate from title allocation and GPU submission.

```mermaid
flowchart LR
    A[Common CPU write workload] --> B[SystemUpload]
    A --> C[MirroredLocal]
    A --> D[GpuUpload]
    A --> E[ExistingSystemHeap]
    B --> F[GPU checksum]
    C --> F
    D --> F
    E --> F
    F --> G[Readback validation and timing]
```

Four backing strategies use a common checksum workload. Existing-system-memory heaps and GPU upload depend on hardware and driver support. No title performance advantage has been established.

## Managed graphics ABI

```mermaid
flowchart TD
    A[Console native call] --> B[Unmanaged C# callback]
    B --> C[Console-layout object / vtable]
    C --> D[Supported ABI handling]
    D --> E[Host COM function pointer]
    E --> F[TerraFX / D3D12]
    F --> G[Host GPU]
    C --> H[Unsupported-slot diagnostics]
    D -.-> I[Experimental shader and resource translation]
```

Source: [Graphics](../IraRationis.Graphics). Separate console and host layouts, explicit unmanaged calls and direct COM dispatch. Newer source includes host resource materialization, pipeline metadata, texture computer interfaces, query heaps and host DXR capability detection. Guest ray tracing, complete shader translation, coherence and original frame presentation are not established.

## Build-time ABI fallbacks

```mermaid
flowchart LR
    A[Roslyn incremental generator] --> B[Generated callbacks and lookup]
    B --> C[Compiled application]
    C --> D[Graphics dispatch table]
    D --> E[Explicit known method]
    D --> F[Diagnostic fallback]
```

[Generator](../IraRationis.Generators/MissingGraphicsSlotGenerator.cs). Build-time generation avoids shipping Roslyn as a runtime dependency. Known ABI registrations remain explicit.

## Build-aware compatibility diagnostics

```mermaid
flowchart TD
    A[Title build identification] --> B[Import and ABI preflight]
    B --> C[Compatibility inventory]
    C --> D[Execution]
    D --> E[Observed unsupported ABI]
    E --> F[Persisted blocker history]
    F -.-> B
```

This describes the diagnostic workflow reported in the existing README, not a complete static call graph. Compare logs against the exact code revision before attributing any runtime result.

## Validation boundary

Maintainer-reported Debug/JIT execution reached partial graphics initialization. Synthetic probes are not title rendering. Keep original frame, intro, menu, gameplay and Native AOT claims separate until reproduced. The latest repository source was not compiled or executed during this documentation review.
