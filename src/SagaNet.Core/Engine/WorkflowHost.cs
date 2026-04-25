using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SagaNet.Core.Abstractions;
using SagaNet.Core.Diagnostics;
using SagaNet.Core.Models;

namespace SagaNet.Core.Engine;

/// <summary>
/// <see cref="IHostedService"/> that continuously polls for runnable workflow instances
/// and dispatches them to <see cref="WorkflowExecutor"/> with configurable concurrency.
/// Also handles external event delivery to waiting workflows.
/// </summary>
public sealed class WorkflowHost : BackgroundService, IWorkflowHost
{
    private readonly IWorkflowRepository _repository;
    private readonly IWorkflowRegistry _registry;
    private readonly WorkflowExecutor _executor;
    private readonly IEventBroker _eventBroker;
    private readonly WorkflowOptions _options;
    private readonly ILogger<WorkflowHost> _logger;

    // Channel used to enqueue instance IDs discovered during polling.
    private readonly Channel<Guid> _workQueue;

    public WorkflowHost(
        IWorkflowRepository repository,
        IWorkflowRegistry registry,
        WorkflowExecutor executor,
        IEventBroker eventBroker,
        IOptions<WorkflowOptions> options,
        ILogger<WorkflowHost> logger)
    {
        _repository = repository;
        _registry = registry;
        _executor = executor;
        _eventBroker = eventBroker;
        _options = options.Value;
        _logger = logger;

        _workQueue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(
            _options.PollBatchSize * 2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true
        });
    }

    // ── IWorkflowHost ────────────────────────────────────────────────────────

    public async Task<Guid> StartWorkflowAsync<TWorkflow, TData>(
        TData data,
        CancellationToken cancellationToken = default)
        where TWorkflow : IWorkflow<TData>
        where TData : class, new()
    {
        var definition = ResolveDefinition<TWorkflow, TData>();

        var instance = new WorkflowInstance
        {
            WorkflowName = definition.Name,
            Version = definition.Version,
            DataType = typeof(TData).AssemblyQualifiedName ?? typeof(TData).FullName!,
            DataJson = System.Text.Json.JsonSerializer.Serialize(data),
            Status = WorkflowStatus.Runnable,
            NextExecutionTime = DateTime.UtcNow
        };

        instance = await _repository.CreateInstanceAsync(instance, cancellationToken);

        SagaNetDiagnostics.WorkflowsStarted.Add(1,
            new System.Diagnostics.TagList { { "workflow", definition.Name } });
        SagaNetDiagnostics.ActiveWorkflows.Add(1,
            new System.Diagnostics.TagList { { "workflow", definition.Name } });

        _logger.LogInformation(
            "Started workflow {WorkflowName} v{Version} [{InstanceId}]",
            definition.Name, definition.Version, instance.Id);

        // Eagerly enqueue for fast pick-up.
        _workQueue.Writer.TryWrite(instance.Id);

        return instance.Id;
    }

    public async Task PublishEventAsync(
        string eventName,
        string eventKey,
        object? eventData = null,
        CancellationToken cancellationToken = default)
    {
        var @event = new WorkflowEvent
        {
            EventName = eventName,
            EventKey = eventKey,
            EventDataJson = eventData is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(eventData)
        };

        await _repository.PublishEventAsync(@event, cancellationToken);

        // Also notify in-process waiters.
        await _eventBroker.PublishAsync(eventName, eventKey, eventData, cancellationToken);

        _logger.LogDebug("Published event {EventName}/{EventKey}", eventName, eventKey);
    }

    public async Task TerminateWorkflowAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await _repository.GetInstanceAsync(instanceId, cancellationToken);
        if (instance is null || instance.IsTerminal) return;

        instance.Status = WorkflowStatus.Terminated;
        instance.CompleteAt = DateTime.UtcNow;
        await _repository.UpdateInstanceAsync(instance, cancellationToken);

        _logger.LogInformation("Terminated workflow [{InstanceId}]", instanceId);
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WorkflowHost started (poll={PollInterval}, batch={Batch}, concurrency={Concurrency})",
            _options.PollInterval, _options.PollBatchSize, _options.MaxConcurrentWorkflows);

        // Start concurrent workers.
        var workers = Enumerable
            .Range(0, _options.MaxConcurrentWorkflows)
            .Select(_ => WorkerLoopAsync(stoppingToken))
            .ToArray();

        // Poll loop feeds the work queue.
        await PollLoopAsync(stoppingToken);

        _workQueue.Writer.Complete();
        await Task.WhenAll(workers);

        _logger.LogInformation("WorkflowHost stopped.");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var ids = await _repository.GetRunnableInstanceIdsAsync(_options.PollBatchSize, ct);
                foreach (var id in ids)
                    _workQueue.Writer.TryWrite(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling for runnable workflow instances.");
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        await foreach (var instanceId in _workQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await _executor.ExecuteAsync(instanceId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unhandled error processing workflow instance [{InstanceId}]", instanceId);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private WorkflowDefinition ResolveDefinition<TWorkflow, TData>()
        where TWorkflow : IWorkflow<TData>
        where TData : class, new()
    {
        // Instantiate temporarily just to get name/version.
        var wf = (IWorkflow<TData>)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(TWorkflow));

        var def = _registry.Find(wf.Name, wf.Version);
        if (def is null)
            throw new Exceptions.WorkflowException(
                $"Workflow '{wf.Name}' v{wf.Version} is not registered. " +
                $"Call builder.AddWorkflow<{typeof(TWorkflow).Name}>() during startup.");

        return def;
    }
}
