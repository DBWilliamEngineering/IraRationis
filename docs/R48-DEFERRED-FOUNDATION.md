# R48 | Deferred preparation and retirement foundation

Base: f481a1207744a357e20d20b339e495553ed58722. All files are full drop-ins; SDK default compile globs include the new Core files. No new dependency or project is required.

## Deferred CPU preparation

`DeferredPreparationQueue<TInput,TPrepared>` overlaps independently owned CPU snapshots with ordered consumption. It is single-owner-thread, bounded by both admission capacity and a worker semaphore, and uses the managed thread pool rather than dedicating threads. `TryEnqueue` returns false at capacity; `TryTakeNext` never waits and `TakeNext` waits for the oldest admitted item. Failure propagates when consumed; shutdown cancels and joins outstanding work. Inputs/results are managed snapshots only, not COM handles or pointers. Workers must cooperate with cancellation and not reenter the queue. Cancellation is not a forced termination deadline. This API is not wired into the existing guest dispatcher.

## Resource retirement

`FenceRetirementQueue<T>` retains unique disposable leases until the owner observes actual completion on a single monotonic fence. `TryRetire` refuses admission when full without taking ownership. `Collect` releases completed entries in order; the device-removed sentinel is rejected. `EnsureEmpty` verifies shutdown, not forced freeing. Leases must be nonthrowing and nonreentrant. The queue does not submit GPU work, signal fences, own native objects directly, or determine resource state. It is not wired into existing guest resource ownership.

## Host audiovisual evidence

Set `IRX_HOST_MEDIA_DIAGNOSTICS=1` before running the existing authorized host media workflow. Output includes validated dimensions and PCM peak, initialization stages, audio start, clock advancement, first accepted frame, completion/cancellation or failing stage. It reports neither visible content verification nor physical audio output. No title-specific hooks or account handling are changed. A log with no host media markers does not establish that any guest decoder was or was not invoked.

## Reference and limitations

Conceptual reference: https://github.com/avatarmanz/VIctory-7900XTX (README, deferred preparation and completion-aware resource publication). This is an independent C# implementation; no reference repository code was copied. No claim of performance recovery or guest movie integration. Build and Windows GPU execution must be verified on the target machine. No additional test executable is included.
