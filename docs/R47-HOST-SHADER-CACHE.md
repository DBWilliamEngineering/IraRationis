# R47: host media shader compilation cache

Baseline: 8725bddc4277097cfcb8b0886621c02f76f84704 (R46).

The existing host NV12 compositor now obtains fixed-profile `cs_5_1` bytecode
through a process-wide bounded cache. It is used by the existing R42 conversion
and R43/R44/R45 presentation paths. The R44 device-local pipeline cache remains
in place. Device-specific root signatures and PSOs are never shared across
contexts by this new cache.

## Contracts

- Exact source-string key; compiler/profile/options are fixed within the process.
- Identical admitted requests share a single Lazy compilation; distinct requests
  can compile independently. Compilation and reporting occur outside the cache lock.
- Retains at most 32 entries and 8 MiB of completed shader bytecode; source strings
  over 64 Ki characters are not cached. These are cache-retention bounds, not a
  process-wide limit on concurrent compilation or caller-held arrays.
- Least-recently-used completed entries are evicted. Pending entries cannot be
  evicted; when all entries are pending, a new key compiles without being cached.
- Failed compilation removes the matching cache entry. No negative cache and no
  fabricated shader. Oversized artifacts are returned without being retained.
- Bytecode is internal and read-only by convention. Existing synchronous PSO
  creation pins it without mutation. Eviction has no effect on COM object lifetime.
- No filesystem cache, network access, external libraries, guest memory changes,
  title-specific shader matching, account changes, or entitlement changes.

Set `IRX_HOST_MEDIA_SHADER_CACHE=0` before process startup for the old compile path.
The existing compiler and D3D12 PSO error checks remain authoritative. Repeated
independent host pipeline creations in the same process may log
`IRX-R47: host shader cache reused`. R44's PSO cache hits need not reach this cache.

## Scope and validation

This reduces repeated host shader compilation, not steady-state GPU submission
latency. Guest shader translation, guest command scheduling and cross-queue
resource publication are unchanged. This is not a demonstrated frame-rate fix.
No new test executable is supplied. Build/publish using the existing solution.
Windows/.NET 11 build and hardware performance have not been validated here.
