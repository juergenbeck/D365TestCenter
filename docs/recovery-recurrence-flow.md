# Stale-Chunk Recovery: Recurrence Flow Setup

The worker model (ADR-0009) fans a test run out into `jbe_testchunk` records that async worker
plugins process. If a single wave hits the hard 120-second Dataverse sandbox limit, the worker is
killed **after** it claimed the chunk (`jbe_chunkstatus = Running`) but **before** it flips the
chunk forward. A frozen chunk receives no further trigger event, so the run never reaches its
plateau and never completes (deadlock; see fault catalog FB-46).

The recovery sweep resolves this: it finds chunks stuck in "Running" for longer than a threshold
(provably dead, because a live worker holds "Running" for at most 120 s) and resets them to
"Resume", so the worker continues from `jbe_group_cursor`. A loop breaker poisons a chunk that keeps
timing out without progress (the irreducible case of a single test whose step chain exceeds the wave
budget).

The sweep runs as an **asynchronous** step behind the global custom API `jbe_RecoverStaleChunks`
(it uses optimistic-concurrency `try/catch`, which is only sandbox-safe in async plugins). A
**Power Automate recurrence flow** is the recurring tick that calls the custom API.

## Prerequisites

1. Schema deployed (`Add-WorkerSchema.ps1`): adds `jbe_lastclaimedon` and `jbe_recoverycount` on
   `jbe_testchunk`.
2. Plugin package deployed (`Deploy-PluginPackage.ps1`) with the `RecoverStaleChunks` plugin type.
3. Custom API + async step registered (`Register-RecoveryApi.ps1`).

## Create the recurrence flow

In Power Automate (same environment as the org):

1. **New > Scheduled cloud flow.**
2. Recurrence: every **5 minutes** (any interval clearly above the stale threshold works; 5 min is a
   good default backstop).
3. Add action: **Microsoft Dataverse > Perform an unbound action.**
   - **Action Name:** `jbe_RecoverStaleChunks`
   - No parameters.
4. Save and turn the flow **On**.

That is the entire flow: a timer that invokes the custom API. All recovery logic lives in the
tested C# core (`StaleChunkRecoveryService`), not in the flow.

### Alternative without Power Automate

Any recurring caller works. The custom API is an unbound (global) action:

```
POST {org}/api/data/v9.2/jbe_RecoverStaleChunks
Content-Type: application/json
{}
```

A scheduled script (CLI / Azure Function timer / cron with an OAuth token) calling that endpoint
is an equivalent tick.

## Tuning (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `jbe_stale_chunk_seconds` | 180 | Seconds a chunk may stay in "Running" before it is treated as dead. Must stay safely above the 120 s sandbox limit. |
| `jbe_max_recoveries` | 3 | Recoveries of one chunk without progress before it is poisoned (set to "Error"). The worker resets this counter to 0 on every successful wave. |

Worst-case recovery latency for a stuck chunk is roughly `jbe_stale_chunk_seconds` plus the flow
interval (default ~3 + 5 = 8 minutes). Lower the interval if faster recovery is required; keep it
above the sweep duration to avoid heavy overlap (overlapping sweeps are safe but wasteful).

## What the sweep does per run (status "Running")

1. Find chunks in status "Running" whose `jbe_lastclaimedon` (fallback `jbe_startedon`) is older than
   `jbe_stale_chunk_seconds`.
2. For each: if `recoverycount + 1 > jbe_max_recoveries`, set the chunk to "Error" with a diagnostic
   message (loop breaker); otherwise reset it to "Resume" and increment `jbe_recoverycount`. Both via
   optimistic concurrency, so a chunk that a late worker just flipped is skipped, not clobbered.
3. Re-check the run plateau (`chunks_done + chunks_failed == chunks_total`) and complete the run if
   reached. A poisoned chunk counts as failed, so a run with an irrecoverable chunk completes cleanly
   instead of deadlocking.

## Verifying

- Live worker chunk (claimed seconds ago): **not** reset.
- Frozen chunk (Running > threshold): reset to "Resume", worker continues, run reaches plateau.
- Repeatedly timing-out chunk: poisoned to "Error" after `jbe_max_recoveries`; run completes.

Trace output of the async step reports `runs=… recovered=… poisoned=… completed=…` per sweep.
