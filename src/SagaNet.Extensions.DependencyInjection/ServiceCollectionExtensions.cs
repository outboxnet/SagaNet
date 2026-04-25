using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SagaNet.Core.Abstractions;
using SagaNet.Core.Engine;
using SagaNet.Core.Events;
using SagaNet.Core.Middleware;
using SagaNet.Core.Models;
using SagaNet.Core.Registry;

namespace SagaNet.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering SagaNet into an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the SagaNet workflow engine to the DI container.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddSagaNet(builder =>
    /// {
    ///     builder
    ///         .AddWorkflow&lt;OrderWorkflow, OrderData&gt;()
    ///         .Configure(opt => opt.PollInterval = TimeSpan.FromSeconds(1));
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddSagaNet(
        this IServiceCollection services,
        Action<SagaNetBuilder>? configure = null)
    {
        // Options
        services.AddOptions<WorkflowOptions>()
            .BindConfiguration(WorkflowOptions.SectionName);

        // Core services
        services.TryAddSingleton<IWorkflowRegistry>(sp =>
        {
            var registry = new WorkflowRegistry(sp);
            var registrations = sp.GetServices<WorkflowRegistration>();

            foreach (var reg in registrations)
            {
                var method = typeof(IWorkflowRegistry)
                    .GetMethod(nameof(IWorkflowRegistry.Register))!
                    .MakeGenericMethod(reg.WorkflowType, reg.DataType);
                method.Invoke(registry, null);
            }

            return registry;
        });

        services.TryAddSingleton<IEventBroker, InMemoryEventBroker>();

        // Engine internals
        services.TryAddTransient<StepExecutor>();
        services.TryAddTransient<SagaCompensator>();
        services.TryAddTransient<WorkflowExecutor>();

        // Built-in middleware (order matters — ObservabilityMiddleware should wrap RetryMiddleware)
        services.AddTransient<IWorkflowMiddleware, ObservabilityMiddleware>();
        services.AddTransient<IWorkflowMiddleware, RetryMiddleware>();

        // WorkflowHost acts as both IHostedService and IWorkflowHost
        services.TryAddSingleton<WorkflowHost>();
        services.TryAddSingleton<IWorkflowHost>(sp => sp.GetRequiredService<WorkflowHost>());
        services.AddHostedService(sp => sp.GetRequiredService<WorkflowHost>());

        // Apply builder configuration
        var builder = new SagaNetBuilder(services);
        configure?.Invoke(builder);

        return services;
    }

    /// <summary>
    /// Adds SagaNet health checks: reports Healthy when the host is running.
    /// </summary>
    public static IHealthChecksBuilder AddSagaNetHealthCheck(
        this IHealthChecksBuilder builder,
        string name = "saganet-workflow-host")
    {
        builder.AddCheck<WorkflowHostHealthCheck>(name);
        return builder;
    }
}
