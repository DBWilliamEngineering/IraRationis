# R53: Runtime utility execution integration

Baseline: `d18868543526c75005b12bad3fd1666037814340`.

This revision uses the same completion-owned execution context in the existing
runtime texture-upload and compute-dispatch paths, and the existing R52 native
GPU validation. It adds no test project and no new title-specific shader or ABI.

## Runtime design

- Reuse a DIRECT allocator/list, upload resource and optional shader-visible heap
  after a real host completion fence. Aborted recording is destroyed, not pooled.
- Bound idle contexts to eight and idle upload allocations to 64 MiB of logical
  buffer capacity; active allocations retain the previous synchronous semantics.
- Match exact upload capacity, descriptor count and device. Versioned contexts
  additionally match the owning managed thread (R51 is single-owner).
- R17's explicitly captured constant bytes use R51 in 256-byte comparison blocks;
  unchanged blocks need no new CPU writes into the UPLOAD heap. This is not a
  DEFAULT-heap transfer optimization or a detector of arbitrary pointer writes.
- R16 texture staging still reads and detiles the source on every upload; no
  stale-content optimization or new alias assumption is introduced.
- Runtime texture descriptors are rewritten on each acquisition. R52's existing
  buffer table cache is exercised with the same utility context in the probe;
  it is not presented as general runtime texture descriptor virtualization.
- All previous resource transitions, queue ordering and host completion waits
  remain. Asynchronous submission is out of scope until recording ownership is
  detached from guest reset/release lifetimes.

## Reference and provenance

Consulted the public GPL-3.0 Victory fork at
`85d564167324056381327ff50f52da85048c84e3`:
`crates/victory-gpu/src/descriptor/host.rs` (descriptor ownership and reuse) and
`crates/victory-gpu/src/queue/behind.rs` (retirement, bounded work and ordering).
R53 implements a different, synchronous, bounded native utility context with
R51 versioning; no Rust source copied in this change. Existing historical
provenance and licenses remain unchanged.

## Validation

Run existing R50/R51/R52 contracts, then `graphics-probe` with an AOT executable.
The existing R52 four-dispatch fixture now changes one constant, restores it,
and then repeats an unchanged upload while retaining the same native descriptor
heap and validating every GPU result. Expect R52's original hit/miss counters
and `IRX-R53: initialBytes=560; changedBytes=256; restoredBytes=256;
unchangedBytes=0; readback=verified.` Initial bytes may be zero when the same
completed context is reused with identical initial data (e.g. newer ABI).

Use an authorized runtime workload to observe `IRX-R53-UTILITY` counters.
Set `IRX_RUNTIME_UTILITY_POOL=0` before launch to disable idle reuse for A/B comparisons.
Compare creation/reuse counts and actual timings against R52, not isolated
media-probe numbers. Counters are process cumulative. A missing marker means
these specific production paths did not execute; it is not evidence of success.
No claims of new game rendering coverage or generalized shader compatibility.
