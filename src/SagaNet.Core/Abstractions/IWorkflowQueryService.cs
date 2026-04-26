using SagaNet.Core.Models;

namespace SagaNet.Core.Abstractions;

/// <summary>
/// Read-side service for querying workflow task progress.
/// Inject this into your API controllers or minimal-API handlers to expose
/// progress data to a frontend.
/// </summary>
public interface IWorkflowQueryService
{
    /// <summary>
    /// Returns a progress snapshot for the given workflow instance ID,
    /// or null if the instance does not exist.
    /// </summary>
    Task<WorkflowProgress?> GetProgressAsync(
        Guid instanceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns progress snapshots for all workflow instances that share
    /// the given correlation ID (e.g. an external order ID).
    /// </summary>
    Task<IReadOnlyList<WorkflowProgress>> GetProgressByCorrelationIdAsync(
        string correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent workflow instances, newest first.
    /// Useful for admin dashboards.
    /// </summary>
    Task<IReadOnlyList<WorkflowProgress>> GetRecentAsync(
        int limit = 50,
        CancellationToken ct = default);
}
