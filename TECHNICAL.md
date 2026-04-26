# SagaNet — Complete Technical Documentation

This document explains how SagaNet works **behind the scenes**, from the moment you call `StartWorkflowAsync` to the moment a workflow completes or rolls back. It assumes you know basic C# but have never looked at a workflow engine before.

---

## Table of Contents

1. [Bird's-eye view of the architecture](#1-birds-eye-view-of-the-architecture)
2. [The three layers](#2-the-three-layers)
3. [How a workflow is defined — the Builder](#3-how-a-workflow-is-defined--the-builder)
   - [What the fluent API actually does](#what-the-fluent-api-actually-does)
   - [What gets built: StepDefinition and WorkflowDefinition](#what-gets-built-stepdefinition-and-workflowdefinition)
   - [How compensation steps fit in](#how-compensation-steps-fit-in)
4. [The Registry — the in-memory catalogue](#4-the-registry--the-in-memory-catalogue)
5. [Starting a workflow — what happens when you call StartWorkflowAsync](#5-starting-a-workflow--what-happens-when-you-call-startworkflowasync)
6. [The WorkflowHost — background engine](#6-the-workflowhost--background-engine)
   - [The poll loop](#the-poll-loop)
   - [The work queue and concurrent workers](#the-work-queue-and-concurrent-workers)
   - [In-flight deduplication](#in-flight-deduplication)
7. [The WorkflowExecutor — one tick at a time](#7-the-workflowexecutor--one-tick-at-a-time)
   - [Acquiring a lock on the instance](#acquiring-a-lock-on-the-instance)
   - [The tick — finding the next step to run](#the-tick--finding-the-next-step-to-run)
   - [Applying the result — what happens after a step runs](#applying-the-result--what-happens-after-a-step-runs)
8. [The StepExecutor — running one step](#8-the-stepexecutor--running-one-step)
   - [Resolving the step from DI](#resolving-the-step-from-di)
   - [Building the execution context](#building-the-execution-context)
   - [Running through the middleware pipeline](#running-through-the-middleware-pipeline)
9. [The Middleware Pipeline](#9-the-middleware-pipeline)
   - [RetryMiddleware](#retrymiddleware)
   - [ObservabilityMiddleware](#observabilitymiddleware)
   - [Adding your own middleware](#adding-your-own-middleware)
10. [Saga Compensation — automatic rollback](#10-saga-compensation--automatic-rollback)
    - [How the compensator walks backwards](#how-the-compensator-walks-backwards)
    - [The difference between CompensateWithStepIndex and IsCompensatable](#the-difference-between-compensatewithstepindex-and-iscompensatable)
11. [Waiting for external events](#11-waiting-for-external-events)
    - [How a workflow suspends](#how-a-workflow-suspends)
    - [How a workflow wakes up](#how-a-workflow-wakes-up)
    - [The race condition guard](#the-race-condition-guard)
12. [The Persistence Layer](#12-the-persistence-layer)
    - [The three tables](#the-three-tables)
    - [Optimistic concurrency with RowVersion](#optimistic-concurrency-with-rowversion)
    - [How execution pointers are synced to the database](#how-execution-pointers-are-synced-to-the-database)
    - [How the poller avoids stale data](#how-the-poller-avoids-stale-data)
13. [The Query Service — reading progress](#13-the-query-service--reading-progress)
    - [Joining DB data with the registry](#joining-db-data-with-the-registry)
    - [How ProgressPercent is calculated](#how-progresspercent-is-calculated)
14. [Dependency Injection wiring](#14-dependency-injection-wiring)
    - [What AddSagaNet registers](#what-addságanet-registers)
    - [Why the engine uses IServiceScopeFactory](#why-the-engine-uses-iservicescopefactory)
15. [Observability — logs, traces, metrics](#15-observability--logs-traces-metrics)
16. [Complete data-flow walkthrough](#16-complete-data-flow-walkthrough)
17. [Key design decisions and trade-offs](#17-key-design-decisions-and-trade-offs)

---

## 1. Bird's-eye view of the architecture

```
┌───────────────────────────────────────────────────────────────────────────┐
│  Your application code                                                    │
│  ─────────────────────                                                    │
│  IWorkflowHost.StartWorkflowAsync(...)   ← starts a workflow              │
│  IWorkflowHost.PublishEventAsync(...)    ← resumes a waiting workflow     │
│  IWorkflowQueryService.GetProgressAsync ← reads progress for a frontend   │
└─────────────────┬─────────────────────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  SagaNet.Core                                                           │
│  ─────────────                                                          │
│  WorkflowHost (BackgroundService)                                       │
│    ├─ PollLoop         ← scans DB every N ms for Runnable instances     │
│    ├─ WorkQueue        ← Channel<Guid> fed by the poll loop             │
│    └─ Workers (N)      ← read from queue, each gets its own DI scope   │
│         │                                                               │
│         ▼                                                               │
│  WorkflowExecutor      ← loads instance, runs one step (one "tick")    │
│    ├─ StepExecutor     ← resolves step from DI, runs middleware chain   │
│    │    ├─ RetryMiddleware          ← catches exceptions, retries       │
│    │    ├─ ObservabilityMiddleware  ← logs, traces, metrics             │
│    │    └─ Your step's RunAsync()                                       │
│    └─ SagaCompensator  ← reverses completed steps on failure           │
└─────────────────┬───────────────────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  SagaNet.Persistence.EfCore                                             │
│  ──────────────────────────                                             │
│  EfWorkflowRepository   ← reads/writes WorkflowInstances table         │
│  EfWorkflowQueryService ← joins DB + Registry for frontend queries     │
│  SagaNetDbContext        ← EF Core DbContext                           │
│                                                                         │
│  SQL Server (or InMemory for dev)                                       │
│  ├─ WorkflowInstances    ← one row per workflow run                    │
│  ├─ ExecutionPointers    ← one row per step per workflow run           │
│  └─ WorkflowEvents       ← queue of external events                   │
└─────────────────────────────────────────────────────────────────────────┘
```

The three projects (`Core`, `Persistence.EfCore`, `Extensions.DependencyInjection`) are intentionally separated so you can swap out the persistence layer without touching the engine.

---

## 2. The three layers

| Layer | Project | Responsibility |
|---|---|---|
| **Engine** | `SagaNet.Core` | All execution logic — no infrastructure dependencies |
| **Persistence** | `SagaNet.Persistence.EfCore` | SQL Server storage via EF Core |
| **Wiring** | `SagaNet.Extensions.DependencyInjection` | Registers everything in the ASP.NET DI container |

`SagaNet.Core` only references `Microsoft.Extensions.*` abstractions (logging, hosting, DI interfaces). It has **zero** knowledge of EF Core or SQL Server. This means you could write a `SagaNet.Persistence.Redis` or `SagaNet.Persistence.Cosmos` implementation just by implementing `IWorkflowRepository`.

---

## 3. How a workflow is defined — the Builder

### What the fluent API actually does

When you write:

```csharp
public class OrderWorkflow : IWorkflow<OrderData>
{
    public string Name => "OrderWorkflow";

    public void Build(IWorkflowBuilder<OrderData> builder)
    {
        builder
            .StartWith<ValidateOrderStep>("ValidateOrder")
            .Then<ReserveInventoryStep>("ReserveInventory")
                .CompensateWith<ReserveInventoryStep>()
            .Then<ProcessPaymentStep>("ProcessPayment")
                .CompensateWith<ProcessPaymentStep>()
            .Then<ShipOrderStep>("ShipOrder");
    }
}
```

`builder` is an instance of `WorkflowBuilder<TData>`. Internally it owns a `List<MutableStepDefinition>` — a mutable scratch-pad that accumulates the step graph as you chain calls.

Here is what each call does:

```
StartWith<ValidateOrderStep>("ValidateOrder")
  → creates MutableStepDefinition { Index=0, Name="ValidateOrder", StepType=ValidateOrderStep }
  → adds it to the list
  → returns StepBuilder(current=step[0])

.Then<ReserveInventoryStep>("ReserveInventory")
  → called on StepBuilder(current=step[0])
  → creates MutableStepDefinition { Index=1, Name="ReserveInventory", ... }
  → adds index 1 to step[0].NextStepIndices   ← THIS is how the link is made
  → adds step[1] to the list
  → returns StepBuilder(current=step[1])

.CompensateWith<ReserveInventoryStep>()
  → called on StepBuilder(current=step[1])
  → creates MutableStepDefinition { Index=2, Name="ReserveInventory", IsCompensationStep=true }
  → sets step[1].CompensateWithStepIndex = 2
  → does NOT add to any NextStepIndices (compensation steps are not in the forward flow)
  → returns the SAME StepBuilder(current=step[1])   ← note: .Then() after this still uses step[1]

.Then<ProcessPaymentStep>("ProcessPayment")
  → called on StepBuilder(current=step[1])
  → creates step[3] { Index=3, ... }
  → adds index 3 to step[1].NextStepIndices
  → returns StepBuilder(current=step[3])

.CompensateWith<ProcessPaymentStep>()
  → creates step[4] { Index=4, IsCompensationStep=true }
  → sets step[3].CompensateWithStepIndex = 4
  → returns StepBuilder(current=step[3])

.Then<ShipOrderStep>("ShipOrder")
  → creates step[5] { Index=5 }
  → adds index 5 to step[3].NextStepIndices
  → returns StepBuilder(current=step[5])
```

After all calls the list looks like this:

```
Index  Name                  Type                  IsComp  NextStepIndices  CompensateWith
──────────────────────────────────────────────────────────────────────────────────────────
0      ValidateOrder         ValidateOrderStep     false   [1]              -
1      ReserveInventory      ReserveInventoryStep  false   [3]              2
2      ReserveInventory      ReserveInventoryStep  true    []               -
3      ProcessPayment        ProcessPaymentStep    false   [5]              4
4      ProcessPayment        ProcessPaymentStep    true    []               -
5      ShipOrder             ShipOrderStep         false   []               -
```

**Key insight**: the forward flow is step 0 → 1 → 3 → 5. Steps 2 and 4 are compensation steps. They live in the same step list but have `IsCompensationStep = true` and are never added to any `NextStepIndices`. They only exist so that `SagaCompensator` can find and execute them by index.

### What gets built: StepDefinition and WorkflowDefinition

When `Build(workflowName, version, dataType)` is called on the builder, the mutable scratch-pad is frozen into an **immutable** `WorkflowDefinition`:

```csharp
public sealed class WorkflowDefinition
{
    public string Name { get; init; }
    public int Version { get; init; }
    public Type DataType { get; init; }     // typeof(OrderData)
    public IReadOnlyList<StepDefinition> Steps { get; init; }
}

public sealed class StepDefinition
{
    public int Index { get; init; }
    public string Name { get; init; }
    public Type StepType { get; init; }
    public bool IsCompensatable { get; init; }    // implements ICompensatableStep<T>?
    public bool IsCompensationStep { get; init; } // is this the undo action itself?
    public int? CompensateWithStepIndex { get; init; }
    public RetryPolicy? RetryPolicy { get; init; }
    public IReadOnlyList<int> NextStepIndices { get; init; }
    public string? Description { get; init; }
}
```

This definition object is created once at startup and shared read-only across all workflow executions. It is never mutated at runtime.

### How compensation steps fit in

`IsCompensatable` and `IsCompensationStep` are different flags:

| Flag | Meaning |
|---|---|
| `IsCompensatable` | This step **can** be undone — it implements `ICompensatableStep<T>` |
| `IsCompensationStep` | This step **is** an undo action — it was registered via `.CompensateWith<>()` |

A step can implement `ICompensatableStep<T>` (making `IsCompensatable = true`) without having an explicit compensation step registered. In that case, if compensation is triggered, the engine calls `CompensateAsync` directly on the same step object. The explicit `.CompensateWith<TCompensate>()` method lets you designate a different class to handle rollback if you prefer separating the concerns.

---

## 4. The Registry — the in-memory catalogue

`WorkflowRegistry` is a thread-safe dictionary keyed by `(name, version)`:

```csharp
private readonly ConcurrentDictionary<(string, int), WorkflowDefinition> _definitions = new();
```

At startup, `AddWorkflow<TWorkflow, TData>()` in the DI builder does this:

```
1. Create an uninitialized instance of TWorkflow (no constructor, no allocations)
2. Call workflow.Build(new WorkflowBuilder<TData>())
3. Call builder.Build(workflow.Name, workflow.Version, typeof(TData))
4. Store the resulting WorkflowDefinition in the Registry
```

Step 1 uses `RuntimeHelpers.GetUninitializedObject(typeof(TWorkflow))`. This creates an instance of your class with NO constructor call. It is safe because `Build()` is the only method called, and it does not depend on any constructor-injected fields. This avoids needing your workflow class to be registered in DI or have a parameterless constructor.

At runtime, `WorkflowExecutor` and `EfWorkflowQueryService` look up definitions via `_registry.Find(name, version)`.

---

## 5. Starting a workflow — what happens when you call StartWorkflowAsync

```csharp
var instanceId = await host.StartWorkflowAsync<OrderWorkflow, OrderData>(data, ct);
```

Inside `WorkflowHost.StartWorkflowAsync`:

```
1.  Look up the WorkflowDefinition from the Registry
    (validates the workflow is registered — throws if not)

2.  Create a new WorkflowInstance in memory:
    {
        Id              = Guid.NewGuid()
        WorkflowName    = "OrderWorkflow"
        Version         = 1
        DataType        = typeof(OrderData).AssemblyQualifiedName
        DataJson        = JsonSerializer.Serialize(data)    ← your data is serialized
        Status          = Runnable
        NextExecutionTime = UtcNow
        ExecutionPointers = []    ← empty at creation
    }

3.  Create an async DI scope
4.  Resolve IWorkflowRepository from the scope
5.  Call repository.CreateInstanceAsync(instance)
    → INSERT into WorkflowInstances table

6.  Increment OpenTelemetry counters (WorkflowsStarted, ActiveWorkflows)

7.  Eagerly write the new instance ID to the work queue channel
    → skips waiting for the next poll cycle for faster startup

8.  Return the instance ID to your code
```

Notice that **no steps run here**. The method returns immediately with an ID. The actual execution happens asynchronously in the background engine.

---

## 6. The WorkflowHost — background engine

`WorkflowHost` is an ASP.NET `BackgroundService`. It starts when your application starts and runs forever until the app shuts down.

### The poll loop

```csharp
private async Task PollLoopAsync(CancellationToken ct)
{
    using var timer = new PeriodicTimer(_options.PollInterval);
    while (await timer.WaitForNextTickAsync(ct))
    {
        var ids = await repository.GetRunnableInstanceIdsAsync(batchSize, ct);
        foreach (var id in ids)
            _workQueue.Writer.TryWrite(id);
    }
}
```

Every `PollInterval` milliseconds (default 500ms, sample uses 200ms):

1. A fresh DI scope is created with its own `DbContext`
2. The repository runs: `SELECT TOP N Id FROM WorkflowInstances WHERE Status = Runnable AND (NextExecutionTime IS NULL OR NextExecutionTime <= NOW()) ORDER BY NextExecutionTime`
3. Each ID is written to the `Channel<Guid>`

`NextExecutionTime` is how retry delays work. When a step returns `Retry(TimeSpan.FromSeconds(5))`, the repository sets `NextExecutionTime = UtcNow + 5 seconds`. The poll query won't pick up the instance again until that time passes.

### The work queue and concurrent workers

The channel is a `BoundedChannel<Guid>` with capacity `PollBatchSize * 2`. When it is full, new writes use `DropOldest` — old IDs are dropped because they will be rediscovered on the next poll anyway.

`MaxConcurrentWorkflows` worker tasks all read from the same channel:

```
PollLoop ──writes──► Channel<Guid> ──reads──► Worker 1
                                   ──reads──► Worker 2
                                   ──reads──► Worker 3
                                   ──reads──► Worker 4
```

Each worker runs an infinite loop: read an ID, execute, repeat. The workers run on the thread pool — they do not block threads while awaiting I/O.

### In-flight deduplication

A `ConcurrentDictionary<Guid, byte> _inFlight` tracks which instance IDs are currently being executed:

```csharp
if (!_inFlight.TryAdd(instanceId, 0))
    continue;   // another worker already has this one — skip
try { ... execute ... }
finally { _inFlight.TryRemove(instanceId, out _); }
```

This is important because:
- The same ID can appear in the channel multiple times (written by consecutive poll cycles before a worker picks it up)
- SQL Server's `RowVersion` provides database-level protection against dual execution, but the InMemory EF provider does not enforce `RowVersion`. The in-flight dictionary provides application-level protection that works regardless of database provider.

---

## 7. The WorkflowExecutor — one tick at a time

`WorkflowExecutor.ExecuteAsync(instanceId)` does exactly **one step** per call. This is called a "tick". After one tick, the workflow is saved and the method returns. The next tick happens on the next poll cycle (or immediately if the channel still has the ID queued).

### Acquiring a lock on the instance

```csharp
var instance = await _repository.TryAcquireInstanceAsync(instanceId, ct);
if (instance is null) return;  // someone else claimed it
```

`TryAcquireInstanceAsync` does:

```
1. SELECT the WorkflowInstance with its ExecutionPointers (tracking mode)
2. If Status != Runnable → return null (not ready)
3. Set Status = Running
4. SaveChangesAsync
   → If DbUpdateConcurrencyException (RowVersion mismatch on SQL Server) → return null
5. Map entity to domain object and return it
```

The `Status = Running` update serves as a distributed lock: the row is now marked Running, so any other host that loads it at the same time will find it in a non-Runnable state and skip it.

### The tick — finding the next step to run

```csharp
private async Task TickAsync(WorkflowInstance instance, WorkflowDefinition definition, CancellationToken ct)
{
    // First ever tick: no pointers exist yet, create the first one
    if (instance.ExecutionPointers.Count == 0)
    {
        var first = definition.Steps[0];  // first step in definition order
        instance.ExecutionPointers.Add(new ExecutionPointer
        {
            StepIndex = first.Index,
            StepName  = first.Name,
            Status    = Pending
        });
    }

    // Find a Pending pointer that is ready to run (RetryAfter has elapsed)
    var pointer = instance.ExecutionPointers
        .FirstOrDefault(p => p.Status == Pending && IsReady(p));

    if (pointer is null)
    {
        // Nothing ready — check if we are done
        if (IsComplete(instance, definition))
            instance.Status = Complete;
        else
            instance.Status = Runnable;  // will be picked up again
        return;
    }

    // Run the step
    pointer.Status = Running;
    pointer.StartTime = UtcNow;
    pointer.AttemptCount++;

    var result = await ExecuteStepAsync(instance, definition, stepDef, pointer, ct);
    await ApplyResultAsync(result, instance, definition, stepDef, pointer, ct);
}
```

`IsComplete` returns true when no pointer has status `Pending`, `Running`, or `WaitingForEvent`, and no pointer has status `Failed`.

### Applying the result — what happens after a step runs

`ApplyResultAsync` is a switch on the `ExecutionResult` discriminated union:

```
NextResult
  → pointer.Status = Complete
  → For each index in stepDef.NextStepIndices:
      if no pointer exists yet for that index:
          add new ExecutionPointer { StepIndex, Pending }
  → instance.Status = Runnable

CompleteResult
  → pointer.Status = Complete
  → instance.Status = Complete   ← terminal

RetryResult
  → pointer.Status = Pending     ← remains Pending, will be tried again
  → pointer.RetryAfter = UtcNow + retry.Delay
  → instance.NextExecutionTime = pointer.RetryAfter
  → instance.Status = Runnable

WaitForEventResult
  → pointer.Status = WaitingForEvent
  → pointer.EventName = eventName
  → pointer.EventKey = eventKey
  → instance.Status = Suspended
  → checks database for an already-arrived event (race guard)

FailResult
  → pointer.Status = Failed
  → calls SagaCompensator.CompensateAsync
  → instance.Status = Compensated (if all compensations succeeded)
               or Failed (if any compensation also failed)
```

After `ApplyResultAsync`, control returns to `ExecuteAsync` which calls `UpdateInstanceAsync` to persist everything. This save is the only database write per tick.

---

## 8. The StepExecutor — running one step

`StepExecutor.ExecuteAsync<TData>` is the bridge between the engine and your step class.

### Resolving the step from DI

```csharp
var step = (IWorkflowStep<TData>)_serviceProvider.GetRequiredService(stepDef.StepType);
```

`stepDef.StepType` is the `Type` object stored in `StepDefinition` (e.g. `typeof(ProcessPaymentStep)`). ASP.NET DI resolves it. This is why you must call `services.AddTransient<ProcessPaymentStep>()` — the engine looks up the type by its `Type` object, not by a generic parameter.

Because each `WorkflowExecutor` execution lives in its own DI scope, any scoped services injected into your step class (like `IOrderRepository` with an EF `DbContext`) are properly scoped and disposed after the tick completes.

### Building the execution context

```csharp
var context = new StepExecutionContext<TData>
{
    WorkflowInstanceId = instance.Id,
    Data               = JsonSerializer.Deserialize<TData>(instance.DataJson),
    AttemptCount       = pointer.AttemptCount - 1,  // 0-based
    PersistenceData    = pointer.PersistenceData,
    CancellationToken  = ct
};
```

`context.Data` is a **deserialized copy** of the workflow data. Your step mutates it freely. After the step returns, `instance.DataJson = JsonSerializer.Serialize(context.Data)` — the mutated data is serialized back. This is how data survives across steps and across restarts.

`PersistenceData` is a `Dictionary<string, string>` scoped to the specific execution pointer (not the whole workflow). Use it to save step-level state that needs to survive a retry — e.g. a transaction ID you got from a payment gateway before the network timed out.

### Running through the middleware pipeline

```csharp
var middlewareContext = new MiddlewareContext(instance, definition, stepDef, pointer, ct);

var result = await _pipeline.ExecuteAsync(
    middlewareContext,
    ctx => step.RunAsync(context));  // the innermost delegate
```

The step is never called directly. It is always invoked as the innermost delegate of a middleware chain. See the next section.

---

## 9. The Middleware Pipeline

The middleware pipeline is built once per step execution and runs as a nested chain of delegates, identical in concept to ASP.NET's `app.Use(...)` pipeline.

```
ObservabilityMiddleware
    └─► RetryMiddleware
            └─► step.RunAsync(context)
```

Each middleware receives a `next` delegate and calls it to proceed. It can wrap the call with setup/teardown logic, or short-circuit by returning early.

```csharp
public sealed class MiddlewarePipeline
{
    private readonly IReadOnlyList<IWorkflowMiddleware> _middlewares;

    public Task<ExecutionResult> ExecuteAsync(MiddlewareContext ctx, Func<MiddlewareContext, Task<ExecutionResult>> stepDelegate)
    {
        // Build the chain recursively: outermost first
        Func<MiddlewareContext, Task<ExecutionResult>> chain = stepDelegate;
        foreach (var middleware in _middlewares.Reverse())
        {
            var next = chain;
            chain = c => middleware.ExecuteAsync(c, next);
        }
        return chain(ctx);
    }
}
```

### RetryMiddleware

`RetryMiddleware` is the inner guard. It wraps the step call in a try/catch:

```
try
    result = await next(ctx)     ← calls step.RunAsync
    return result
catch (Exception ex)
    attemptCount = pointer.AttemptCount
    maxAttempts  = stepDef.RetryPolicy?.MaxAttempts ?? options.DefaultRetryPolicy.MaxAttempts

    if attemptCount < maxAttempts:
        delay = CalculateDelay(retryPolicy, attemptCount)
        return ExecutionResult.Retry(delay, ex.Message)
    else:
        return ExecutionResult.Fail(ex.Message, ex)
```

`CalculateDelay` applies exponential backoff: `InitialDelay * BackoffMultiplier^(attemptCount - 1)`, capped at `MaxDelay`.

This means: **your steps do not need try/catch for transient errors**. If `ProcessPaymentStep` throws `HttpRequestException` because the gateway timed out, `RetryMiddleware` catches it and returns `Retry(10s)`. On the next tick the step runs again. After `MaxAttempts` failures, it returns `Fail` which triggers compensation.

### ObservabilityMiddleware

`ObservabilityMiddleware` is the outermost wrapper. It runs around the entire middleware chain:

```
1. Start OpenTelemetry Activity "step.<StepName>"
   → Tagged with workflow name, instance ID, step name
2. Log DEBUG: "Executing step ValidateOrder attempt 1"
3. Record start time
4.   ── call next(ctx) ──
5. Record end time, duration
6. If result is Complete or Next:
   → Log DEBUG: "Step ValidateOrder completed in 42ms"
   → Record saganet.steps.executed counter
   → Record saganet.steps.duration_ms histogram
7. If result is Retry:
   → Log INFO: "Step ValidateOrder retrying in 5s (attempt 1/3): <reason>"
   → Increment saganet.steps.retried counter
8. If result is Fail:
   → Log WARN: "Step ValidateOrder failed: <reason>"
   → Increment saganet.steps.failed counter
9. If exception escapes past RetryMiddleware (should not happen normally):
   → Record exception on the span
   → Log ERROR
10. End Activity
```

### Adding your own middleware

You can inject additional middleware before or after the built-in ones:

```csharp
services.AddSagaNet(b => b
    .AddMiddleware<MyAuditMiddleware>()
    .AddWorkflow<OrderWorkflow, OrderData>());
```

Your class implements `IWorkflowMiddleware`:

```csharp
public class MyAuditMiddleware : IWorkflowMiddleware
{
    public async Task<ExecutionResult> ExecuteAsync(
        MiddlewareContext ctx,
        Func<MiddlewareContext, Task<ExecutionResult>> next)
    {
        // Before step
        await auditLog.RecordStartAsync(ctx.StepDefinition.Name);

        var result = await next(ctx);   // run the rest of the chain

        // After step
        await auditLog.RecordEndAsync(ctx.StepDefinition.Name, result);
        return result;
    }
}
```

Custom middleware is inserted **outside** the built-in middleware, so your middleware wraps the built-in retry and observability logic.

---

## 10. Saga Compensation — automatic rollback

### How the compensator walks backwards

When a step returns `Fail`, `WorkflowExecutor.ApplyResultAsync` calls `SagaCompensator.CompensateAsync<TData>`.

```csharp
// Compensate completed steps in reverse completion order
var completedPointers = instance.ExecutionPointers
    .Where(p => p.Status == Complete)
    .OrderByDescending(p => p.EndTime)  // most recently completed first
    .ToList();

foreach (var pointer in completedPointers)
{
    var stepDef = definition.Steps.First(s => s.Index == pointer.StepIndex);

    // Find the compensation step for this forward step
    StepDefinition? compDef = null;
    if (stepDef.CompensateWithStepIndex.HasValue)
        compDef = definition.Steps.First(s => s.Index == stepDef.CompensateWithStepIndex.Value);
    else if (stepDef.IsCompensatable)
        compDef = stepDef;   // same step handles its own compensation

    if (compDef is null)
        continue;   // this step has no undo action — skip

    pointer.Status = Compensating;
    await _stepExecutor.CompensateAsync<TData>(instance, compDef, pointer, ct);
    pointer.Status = Compensated;
}
```

Notice: compensation **reuses the existing execution pointer** of the forward step. It does not create a new pointer. The same pointer's `Status` goes through: `Complete → Compensating → Compensated`.

If a compensation step itself throws, `pointer.Status = Failed` and the whole compensation returns `false`. The workflow ends with `WorkflowStatus.Failed` instead of `Compensated`.

### The difference between CompensateWithStepIndex and IsCompensatable

Two ways a step can be undone:

**Option A — Separate compensation class** (registered via `.CompensateWith<T>()`):
```csharp
.Then<ReserveInventoryStep>()
    .CompensateWith<ReleaseInventoryStep>()
```
`stepDef.CompensateWithStepIndex = 2` → the compensator looks up step 2 and calls `RunAsync` on a `ReleaseInventoryStep` instance.

**Option B — Self-compensating step** (implements `ICompensatableStep<T>`):
```csharp
public class ReserveInventoryStep : ICompensatableStep<OrderData>
{
    public Task<ExecutionResult> RunAsync(...) { /* reserve */ }
    public Task CompensateAsync(...) { /* release */ }
}
```
`stepDef.IsCompensatable = true`. The compensator calls `step.CompensateAsync(context)` directly instead of `RunAsync`. In this case `.CompensateWith<>()` is not needed (but if you call it, the explicit `CompensateWithStepIndex` takes priority).

---

## 11. Waiting for external events

### How a workflow suspends

A step returns:
```csharp
return ExecutionResult.WaitForEvent("PaymentConfirmed", context.Data.OrderId);
```

`ApplyResultAsync` handles `WaitForEventResult`:
```
pointer.Status = WaitingForEvent
pointer.EventName = "PaymentConfirmed"
pointer.EventKey  = orderId
instance.Status   = Suspended
```

The instance is saved with `Status = Suspended`. `GetRunnableInstanceIdsAsync` filters on `Status = Runnable`, so suspended instances are never polled. The workflow sits in the database doing nothing, consuming zero resources, until the event arrives.

### How a workflow wakes up

```csharp
await host.PublishEventAsync("PaymentConfirmed", orderId, new { TransactionId = "TXN-123" });
```

Inside `WorkflowHost.PublishEventAsync`:

```
1. Serialize the event data to JSON
2. Create a new WorkflowEvent record
3. Create a scope, resolve repository
4. Call repository.PublishEventAsync(event):

   a. INSERT into WorkflowEvents table
   b. SELECT ExecutionPointers WHERE Status=WaitingForEvent
         AND EventName="PaymentConfirmed" AND EventKey=orderId
   c. For each matching pointer:
       → pointer.Status = Pending
       → pointer.EventData = serialized payload
       → pointer.WorkflowInstance.Status = Runnable
       → pointer.WorkflowInstance.NextExecutionTime = UtcNow
   d. SaveChangesAsync

5. Also publish via InMemoryEventBroker (in-process notification for
   single-host setups — the poll loop will pick it up anyway)
```

On the next poll cycle, the instance is discovered as `Runnable` and queued for execution. The step runs again. This time `pointer.EventData` contains the payload, which the step can access via `context.PersistenceData` or by checking `pointer.EventData` through the persistence bag.

### The race condition guard

What if the event is published **before** the step has a chance to register itself as `WaitingForEvent`? (This can happen if the event arrives milliseconds after the step runs on a fast path.)

In `ApplyResultAsync` for `WaitForEventResult`:

```csharp
// Check if the event already arrived
var events = await _repository.ConsumeEventsAsync(wait.EventName, wait.EventKey, ct);
if (events.Count > 0)
{
    pointer.EventData = events[0].EventDataJson;
    pointer.Status    = Pending;        // don't suspend — immediately runnable
    instance.Status   = Runnable;
}
```

If an unconsumed event with the matching name and key is already in the `WorkflowEvents` table, the step skips the suspended state entirely and remains `Pending`. It will re-run on the next tick with the event data available.

---

## 12. The Persistence Layer

### The three tables

**WorkflowInstances** — one row per workflow run:

| Column | Type | Purpose |
|---|---|---|
| `Id` | uniqueidentifier | Primary key, returned to caller |
| `WorkflowName` | nvarchar | e.g. "OrderWorkflow" |
| `Version` | int | Workflow schema version |
| `DataType` | nvarchar | Assembly-qualified name of TData |
| `DataJson` | nvarchar(max) | Serialized workflow data — mutated after each step |
| `Status` | int | Enum: Runnable=0, Running=1, Complete=2, ... |
| `CreatedAt` | datetime2 | When StartWorkflowAsync was called |
| `CompleteAt` | datetime2 | When terminal status was reached |
| `NextExecutionTime` | datetime2 | Earliest time the poller should pick this up |
| `CorrelationId` | nvarchar | Optional external key (e.g. your order ID) |
| `Error` | nvarchar | Last error message if failed |
| `RowVersion` | rowversion | SQL Server auto-managed byte stamp for optimistic concurrency |

**ExecutionPointers** — one row per step per workflow run:

| Column | Type | Purpose |
|---|---|---|
| `Id` | uniqueidentifier | Primary key |
| `WorkflowInstanceId` | uniqueidentifier | Foreign key → WorkflowInstances |
| `StepIndex` | int | Which step in the definition |
| `StepName` | nvarchar | Human-readable name |
| `Status` | int | Pending, Running, Complete, Failed, Compensating, Compensated, WaitingForEvent |
| `AttemptCount` | int | How many times this step has been attempted |
| `StartTime` / `EndTime` | datetime2 | When the step started / finished |
| `RetryAfter` | datetime2 | Don't retry before this time |
| `EventName` / `EventKey` | nvarchar | For WaitingForEvent steps |
| `EventData` | nvarchar(max) | Payload received when the event arrived |
| `Error` | nvarchar | Error from the last failed attempt |
| `PersistenceDataJson` | nvarchar(max) | Step-scoped key/value bag |

**WorkflowEvents** — a persistent queue of external events:

| Column | Type | Purpose |
|---|---|---|
| `Id` | uniqueidentifier | Primary key |
| `EventName` | nvarchar | Event type name |
| `EventKey` | nvarchar | Correlation key (e.g. orderId) |
| `EventDataJson` | nvarchar(max) | Serialized payload |
| `CreatedAt` | datetime2 | When published |
| `IsConsumed` | bit | Has a waiting step already consumed this? |
| `ConsumedAt` | datetime2 | When consumed |

### Optimistic concurrency with RowVersion

SQL Server's `rowversion` (also called `timestamp`) is an 8-byte value that SQL Server **automatically increments** whenever a row is updated. EF Core is configured to use it as a concurrency token:

```csharp
// WorkflowInstanceConfiguration.cs
builder.Property(x => x.RowVersion).IsRowVersion();
```

Here is how it prevents dual execution in a multi-host deployment:

```
Time 0ms: Host A loads instance (RowVersion = 0x0001)
Time 0ms: Host B loads same instance (RowVersion = 0x0001)

Time 1ms: Host A sets Status=Running, SaveChanges
          → SQL: UPDATE WorkflowInstances SET Status=1
                 WHERE Id=@id AND RowVersion=0x0001
          → Rows affected: 1 (success)
          → SQL Server increments RowVersion to 0x0002

Time 2ms: Host B sets Status=Running, SaveChanges
          → SQL: UPDATE WorkflowInstances SET Status=1
                 WHERE Id=@id AND RowVersion=0x0001  ← stale!
          → Rows affected: 0
          → EF Core throws DbUpdateConcurrencyException
          → TryAcquireInstanceAsync catches it, returns null
          → Host B skips this instance
```

### How execution pointers are synced to the database

`UpdateInstanceAsync` in `EfWorkflowRepository` does a diff-based sync. It does NOT delete and re-insert all pointers. Instead:

```csharp
// Load the current state from DB
var entity = await _db.WorkflowInstances
    .Include(x => x.ExecutionPointers)
    .FirstOrDefaultAsync(x => x.Id == instance.Id, ct);

// Build a lookup of what's already in the DB, by pointer ID
var existingById = entity.ExecutionPointers.ToDictionary(p => p.Id);

foreach (var domainPointer in domain.ExecutionPointers)
{
    if (existingById.TryGetValue(domainPointer.Id, out var existing))
        MapPointerToEntity(domainPointer, existing);  // UPDATE existing row
    else
        entity.ExecutionPointers.Add(ToPointerEntity(domainPointer, ...)); // INSERT new row
}

// DELETE any pointers that were removed from the domain model
var domainIds = domain.ExecutionPointers.Select(p => p.Id).ToHashSet();
entity.ExecutionPointers.RemoveAll(p => !domainIds.Contains(p.Id));

await _db.SaveChangesAsync(ct);
```

EF Core's change tracker detects which operations are needed and issues the minimal set of `UPDATE`, `INSERT`, and `DELETE` statements.

### How the poller avoids stale data

Every `GetRunnableInstanceIdsAsync` call creates a **fresh DI scope** with its own `DbContext`. Because each `DbContext` is new, there is no stale EF Core identity map cache. The query always hits the database.

---

## 13. The Query Service — reading progress

`EfWorkflowQueryService` is the read-only view for your API. It is intentionally separate from `EfWorkflowRepository` — the repository is the write side (used by the engine), the query service is the read side (used by your controllers).

### Joining DB data with the registry

The query service enriches raw database data with metadata from the registry:

```csharp
var definition = _registry.Find(entity.WorkflowName, entity.Version);

// Step descriptions come from the definition, not the DB
// (description is code, not data — it doesn't need to be persisted)
var forwardDefs = definition?.Steps
    .Where(s => !s.IsCompensationStep)
    .OrderBy(s => s.Index)
    .ToList();
```

For each forward step definition, it looks up whether an execution pointer exists for that step:

```csharp
var pointers = entity.ExecutionPointers
    .GroupBy(p => p.StepIndex)
    .ToDictionary(g => g.Key,
        g => g.OrderByDescending(p => p.StartTime ?? DateTime.MinValue).First());

foreach (var def in forwardDefs)
{
    pointers.TryGetValue(def.Index, out var pointer);
    steps.Add(MapForwardStep(def.Name, def.Description, pointer));
}
```

If no pointer exists for a step yet (it hasn't started), the step is shown as `Pending` with a null start time. This is how the frontend sees "future" steps before they have run.

### How ProgressPercent is calculated

```csharp
var forwardSteps = steps.Where(s => !s.IsCompensation).ToList();
var totalSteps   = forwardSteps.Count;
var completed    = forwardSteps.Count(s => s.Status == Complete);

var percent = status == WorkflowStatus.Complete
    ? 100                                              // always 100 when done
    : totalSteps == 0
        ? 0
        : (int)Math.Floor(completed * 100.0 / total); // floor to avoid showing 100 prematurely
```

Only forward steps count toward the percentage — compensation steps are not included. A workflow that failed and rolled back will show `ProgressPercent = 0` because no forward steps completed successfully (they were either not reached or were compensated).

---

## 14. Dependency Injection wiring

### What AddSagaNet registers

```csharp
services.AddSagaNet(b => b
    .AddWorkflow<OrderWorkflow, OrderData>()
    .Configure(opt => { ... }));
```

`AddSagaNet` registers the following services:

| Service | Lifetime | What it is |
|---|---|---|
| `IWorkflowRegistry` / `WorkflowRegistry` | Singleton | The in-memory step catalogue — shared across the whole app |
| `IEventBroker` / `InMemoryEventBroker` | Singleton | In-process event signalling |
| `WorkflowOptions` | Singleton (via IOptions) | Configuration values |
| `WorkflowExecutor` | Scoped | Advances one workflow per scope |
| `StepExecutor` | Scoped | Runs one step per scope |
| `SagaCompensator` | Scoped | Compensates steps per scope |
| `MiddlewarePipeline` | Scoped | Built fresh per scope from the registered middlewares |
| `RetryMiddleware` | Scoped | Built-in retry logic |
| `ObservabilityMiddleware` | Scoped | Built-in logging/tracing/metrics |
| `WorkflowHost` | Singleton (hosted service) | Background engine |

`IWorkflowRepository` and `IWorkflowQueryService` are **not** registered by `AddSagaNet`. You register them separately (`services.AddScoped<IWorkflowRepository, EfWorkflowRepository>()`). This is intentional — it keeps the engine package independent of the persistence package.

### Why the engine uses IServiceScopeFactory

`WorkflowHost` is a **Singleton** — it lives for the entire application lifetime. But `EfWorkflowRepository` is **Scoped** — it wraps an EF Core `DbContext` which must be short-lived (one unit of work).

You cannot inject a Scoped service directly into a Singleton because the Singleton outlives the Scope. The standard ASP.NET solution is `IServiceScopeFactory`:

```csharp
// WorkflowHost is a Singleton and holds IServiceScopeFactory (also Singleton)
public WorkflowHost(IServiceScopeFactory scopeFactory, ...)
{
    _scopeFactory = scopeFactory;
}

// Each tick creates a fresh short-lived scope
await using var scope = _scopeFactory.CreateAsyncScope();
var executor = scope.ServiceProvider.GetRequiredService<WorkflowExecutor>();
await executor.ExecuteAsync(instanceId, ct);
// scope is disposed here → DbContext is disposed, connection returned to pool
```

Every workflow tick gets its own `DbContext` instance. When the tick completes (whether it succeeded or failed), `await using` disposes the scope, which disposes the `DbContext`, which returns the database connection to the connection pool. This is correct, efficient, and leaks nothing.

---

## 15. Observability — logs, traces, metrics

### Structured logs

Every component uses `ILogger<T>` with structured logging parameters:

```
DBG  Executing step ReserveInventory (attempt 1) [workflow=OrderWorkflow, instance=abc-123]
DBG  Step ReserveInventory completed in 42.3ms [workflow=OrderWorkflow, instance=abc-123]
INF  Step ProcessPayment retrying in 5s (attempt 1/3): Connection timeout [instance=abc-123]
WRN  Step ProcessPayment failed: Payment declined [instance=abc-123]
WRN  Starting saga compensation for workflow OrderWorkflow [abc-123]
INF  Compensated step ReserveInventory [abc-123]
```

### OpenTelemetry traces

The engine uses `ActivitySource("SagaNet")` to create spans:

```
span: workflow.OrderWorkflow [instance=abc-123]
  └─ span: step.ValidateOrder      [status=ok, duration=2ms]
  └─ span: step.ReserveInventory   [status=ok, duration=18ms]
  └─ span: step.ProcessPayment     [status=error, duration=120ms]
       └─ exception event: HttpRequestException: Connection timeout
```

Note that the parent workflow span is not contiguous — it starts when the first tick begins and ends when the last tick completes, but there can be minutes or hours between spans (during retries or event waits). The trace still shows the full end-to-end execution.

### OpenTelemetry metrics

All metrics use `Meter("SagaNet")`:

```
saganet.workflows.started      Counter    — incremented by StartWorkflowAsync
saganet.workflows.completed    Counter    — incremented when Status=Complete
saganet.workflows.compensated  Counter    — incremented when Status=Compensated
saganet.workflows.failed       Counter    — incremented when Status=Failed
saganet.workflows.active       UpDownCounter — +1 on start, -1 on terminal
saganet.steps.executed         Counter    — every step completion
saganet.steps.retried          Counter    — every retry decision
saganet.steps.failed           Counter    — every permanent step failure
saganet.steps.duration_ms      Histogram  — step wall-clock time in ms
saganet.workflows.duration_ms  Histogram  — end-to-end workflow time in ms
```

All metrics include a `workflow` tag so you can filter by workflow type in Grafana/Prometheus.

---

## 16. Complete data-flow walkthrough

Here is every single thing that happens from `POST /orders` to workflow completion, step by step:

```
──────────────────────────────────────────────────────────────────────────────
PHASE 1: Starting the workflow
──────────────────────────────────────────────────────────────────────────────

① HTTP POST /orders  →  OrdersController.PlaceOrder(req)

② IWorkflowHost.StartWorkflowAsync<OrderWorkflow, OrderData>(data)
   ├─ Registry.Find("OrderWorkflow", 1)  →  WorkflowDefinition
   ├─ Create WorkflowInstance { Status=Runnable, DataJson="{...}", ... }
   ├─ Open DI scope
   ├─ IWorkflowRepository.CreateInstanceAsync(instance)
   │    └─ INSERT INTO WorkflowInstances ...  →  DB row created
   ├─ Close scope  →  DbContext disposed
   ├─ Channel.TryWrite(instanceId)   →  instance ID queued
   └─ Return instanceId to controller  →  HTTP 202 Accepted

──────────────────────────────────────────────────────────────────────────────
PHASE 2: First tick (ValidateOrder)
──────────────────────────────────────────────────────────────────────────────

③ PollLoop fires (200ms later)
   └─ GetRunnableInstanceIdsAsync()  →  SELECT ... Status=Runnable
      → finds our instanceId
   └─ Channel.TryWrite(instanceId)  (already there from phase 1 — that's OK)

④ Worker reads instanceId from channel
   ├─ _inFlight.TryAdd(instanceId)  →  succeeds (not in flight)
   └─ Open DI scope

⑤ WorkflowExecutor.ExecuteAsync(instanceId)
   ├─ TryAcquireInstanceAsync(instanceId)
   │    ├─ SELECT with ExecutionPointers (tracking)
   │    ├─ Status=Runnable → proceed
   │    ├─ Set Status=Running
   │    └─ SaveChanges  →  UPDATE ... RowVersion bumped
   │
   ├─ TickAsync:
   │    ├─ ExecutionPointers.Count==0  →  add pointer{StepIndex=0, Pending}
   │    ├─ pointer = pointer{StepIndex=0}
   │    ├─ pointer.Status = Running, StartTime = now, AttemptCount = 1
   │    │
   │    ├─ StepExecutor.ExecuteAsync<OrderData>(instance, stepDef[0], pointer)
   │    │    ├─ Resolve ValidateOrderStep from DI
   │    │    ├─ Deserialize DataJson → OrderData
   │    │    ├─ Build StepExecutionContext
   │    │    └─ MiddlewarePipeline.Execute:
   │    │         ObservabilityMiddleware
   │    │           └─ start Activity "step.ValidateOrder"
   │    │           RetryMiddleware
   │    │             └─ ValidateOrderStep.RunAsync(context)
   │    │                  ← checks OrderId, Amount  →  ExecutionResult.Next()
   │    │           RetryMiddleware: no exception → pass through
   │    │           ObservabilityMiddleware: log, record metrics, end Activity
   │    │         return ExecutionResult.Next()
   │    │
   │    └─ ApplyResultAsync(Next):
   │         ├─ pointer{0}.Status = Complete, EndTime = now
   │         ├─ stepDef[0].NextStepIndices = [1]
   │         ├─ no pointer for StepIndex=1 yet → add pointer{StepIndex=1, Pending}
   │         └─ instance.Status = Runnable
   │
   └─ UpdateInstanceAsync(instance)
        ├─ SELECT entity with pointers
        ├─ existingById = {}  (pointers were never saved before)
        ├─ Add pointer{0} and pointer{1} as new entities
        └─ SaveChanges  →  INSERT 2 ExecutionPointerEntities
                           UPDATE WorkflowInstances Status=Runnable

   ├─ Close scope  →  DbContext disposed
   └─ _inFlight.TryRemove(instanceId)

──────────────────────────────────────────────────────────────────────────────
PHASE 3: Subsequent ticks (ReserveInventory, ProcessPayment, ShipOrder)
──────────────────────────────────────────────────────────────────────────────

⑥ Same flow repeats for each step:
   Poll → Acquire → Tick → ApplyResult → Save
   Each tick:
     - marks the current step Complete
     - adds a pointer for the next step (if not already present)
     - saves Status=Runnable
   The engine processes one step per tick.

──────────────────────────────────────────────────────────────────────────────
PHASE 4: Final tick (ShipOrder)
──────────────────────────────────────────────────────────────────────────────

⑦ ShipOrderStep.RunAsync returns ExecutionResult.Complete()

⑧ ApplyResultAsync(Complete):
   ├─ pointer{5}.Status = Complete, EndTime = now
   ├─ instance.Status = Complete
   └─ instance.CompleteAt = now

⑨ UpdateInstanceAsync  →  UPDATE WorkflowInstances Status=Complete, CompleteAt=now
                           UPDATE ExecutionPointers pointer{5} Status=Complete

   Metrics: WorkflowsCompleted.Add(1), ActiveWorkflows.Add(-1)
   instance is now in terminal state — poller will never pick it up again

──────────────────────────────────────────────────────────────────────────────
PHASE 5: Reading progress (at any point during execution)
──────────────────────────────────────────────────────────────────────────────

⑩ GET /orders/{instanceId}/progress  →  OrdersController.GetProgress

⑪ EfWorkflowQueryService.GetProgressAsync(instanceId)
   ├─ SELECT WorkflowInstances + ExecutionPointers (AsNoTracking)
   ├─ Registry.Find("OrderWorkflow", 1)  →  WorkflowDefinition
   ├─ For each forward StepDefinition (0, 1, 3, 5):
   │    look up execution pointer by StepIndex
   │    if pointer exists → map with real status, timing, error
   │    if not yet started → show as Pending
   ├─ Calculate ProgressPercent = floor(completedCount / totalForwardSteps * 100)
   └─ Return WorkflowProgress { Steps=[...], ProgressPercent=50, ... }
```

---

## 17. Key design decisions and trade-offs

### One step per tick

The engine advances by exactly one step per database round-trip. This means a workflow with 10 steps needs 10 ticks and 10 database writes.

**Why**: each tick is a complete unit of work — acquire, execute, save. If the application crashes mid-execution, at most one step's work is lost. The workflow will resume from the last saved state on restart.

**Trade-off**: high-throughput scenarios with many fast steps may see database contention. You can increase `MaxConcurrentWorkflows` and reduce `PollInterval` to improve throughput, or batch multiple steps per tick in a custom engine variant.

### Data is serialized between steps

`OrderData` is serialized to JSON and stored in the database after every step. Every step deserializes it fresh.

**Why**: the workflow must survive application restarts. If the data lived only in memory, a crash would lose it. JSON serialization decouples the in-memory representation from the stored representation and tolerates schema evolution.

**Trade-off**: serialization overhead on every step. For most workflows this is negligible (microseconds). If your data is very large, consider only storing identifiers in the workflow data and loading the actual data from your own database in each step.

### The middleware pipeline is built per execution

A new `MiddlewarePipeline` (and all its middleware instances) is created for every step execution, inside the DI scope.

**Why**: it allows middleware to have scoped DI dependencies (e.g. accessing the current DbContext). Singletons would not be able to do this.

**Trade-off**: small allocation overhead per step. In practice this is negligible — the middleware objects are tiny and short-lived.

### Compensation reuses existing pointers

When rollback runs, the existing execution pointer for each compensated step has its status changed (`Complete → Compensating → Compensated`). No new pointers are created.

**Why**: the pointer already has the step's context (start time, attempt count, any step-scoped data). Reusing it keeps the history clean.

**Trade-off**: you cannot distinguish "this step ran forward then was compensated" from "this step completed normally" solely by looking at the pointer — you need to check `Status == Compensated` vs `Status == Complete`.

### The in-flight dictionary vs. database locking

The in-flight `ConcurrentDictionary` in `WorkflowHost` prevents two workers on the **same** host from processing the same instance simultaneously. The database `RowVersion` prevents two workers on **different** hosts from doing so.

**Why two layers**: the in-flight dictionary is faster (in-memory, no DB round-trip) and works with any database provider including InMemory. The RowVersion is the authoritative distributed lock for multi-host scenarios.

**Trade-off**: if an application crashes while a workflow is in-flight, the in-flight entry is lost (it's in-memory). The `RowVersion` ensures the DB row ends up in `Status=Running` with no further updates — a monitoring tool or restart will detect and recover it. An optional `StuckWorkflowRecovery` background task could periodically set old `Running` instances back to `Runnable`.
