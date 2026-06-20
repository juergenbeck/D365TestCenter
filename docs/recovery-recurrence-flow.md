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

## Create the recurrence flow (scripted)

The flow is a `workflow` record (`category=5`) and is created, added to the solution and activated
fully via the Web API by `scripts/Create-RecurrenceFlow.ps1` -- no Maker Portal needed:

```
pwsh ./scripts/Create-RecurrenceFlow.ps1 -OrgUrl https://<org>-dev.crm4.dynamics.com `
    -ClientId <id> -ClientSecret <secret> -TenantId <tenant> `
    -ConnectionReferenceLogicalName <sp-owned-cr-logicalname>
```

The flow is a timer that invokes the custom API (concurrency = 1). All recovery logic lives in the
tested C# core (`StaleChunkRecoveryService`), not in the flow. The script is idempotent (skips if a
flow with the same name exists) and goes into the solution (`MSCRM.SolutionUniqueName`).

### Connection reference (important)

The flow needs a Dataverse connection reference (`shared_commondataserviceforapps`). Pass its logical
name via `-ConnectionReferenceLogicalName`. The activating identity must be able to use it; if
activation fails with `ConnectionAuthorizationFailed`, pick the connection reference owned by / usable
by the deploying service principal. On every target environment a usable connection reference must
exist before activation. The script pre-checks the reference exists and heals `InvalidOpenApiFlow`
(custom API not yet published) by running `PublishAllXml` then retrying activation.

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
