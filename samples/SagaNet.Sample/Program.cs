using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SagaNet.Core.Abstractions;
using SagaNet.Extensions.DependencyInjection;
using SagaNet.Persistence.EfCore;
using SagaNet.Persistence.EfCore.Repositories;
using SagaNet.Sample.Workflows;
using SagaNet.Sample.Workflows.Steps;

// ──────────────────────────────────────────────────────────────────────────────
// Host setup
// ──────────────────────────────────────────────────────────────────────────────

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Debug);
    })
    .ConfigureServices((ctx, services) =>
    {
        // ── Persistence ──────────────────────────────────────────────────────
        // In a real app, use a real SQL Server connection string.
        // For the sample we use InMemory EF provider for zero-setup running.
        services.AddDbContext<SagaNetDbContext>(opt =>
            opt.UseInMemoryDatabase("SagaNetSample"));

        services.AddScoped<IWorkflowRepository, EfWorkflowRepository>();

        // ── Workflow steps (register so DI injects ILogger etc.) ─────────────
        services.AddTransient<ValidateOrderStep>();
        services.AddTransient<ReserveInventoryStep>();
        services.AddTransient<ProcessPaymentStep>();
        services.AddTransient<ShipOrderStep>();

        // ── SagaNet engine ───────────────────────────────────────────────────
        services.AddSagaNet(builder =>
        {
            builder
                .AddWorkflow<OrderWorkflow, OrderData>()
                .Configure(opt =>
                {
                    opt.PollInterval = TimeSpan.FromMilliseconds(200);
                    opt.PollBatchSize = 5;
                    opt.MaxConcurrentWorkflows = 4;
                });
        });

        // ── Health checks ────────────────────────────────────────────────────
        services.AddHealthChecks()
            .AddSagaNetHealthCheck();
    })
    .Build();

// ──────────────────────────────────────────────────────────────────────────────
// Ensure DB schema exists (InMemory doesn't need it, but illustrates the call)
// ──────────────────────────────────────────────────────────────────────────────
using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SagaNetDbContext>()
        .Database.EnsureCreatedAsync();
}

// ──────────────────────────────────────────────────────────────────────────────
// Start the host (starts WorkflowHost in the background)
// ──────────────────────────────────────────────────────────────────────────────
await host.StartAsync();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var workflowHost = host.Services.GetRequiredService<IWorkflowHost>();

// ──────────────────────────────────────────────────────────────────────────────
// Demo: start a successful order
// ──────────────────────────────────────────────────────────────────────────────
logger.LogInformation("=== Starting successful order workflow ===");

var successId = await workflowHost.StartWorkflowAsync<OrderWorkflow, OrderData>(
    new OrderData
    {
        OrderId = "ORD-001",
        Amount = 149.99m,
        CustomerEmail = "customer@example.com"
    });

logger.LogInformation("Workflow started: {InstanceId}", successId);

await Task.Delay(3000); // let it run

// ──────────────────────────────────────────────────────────────────────────────
// Demo: check result via repository
// ──────────────────────────────────────────────────────────────────────────────
using (var scope = host.Services.CreateScope())
{
    var repo = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
    var instance = await repo.GetInstanceAsync(successId);
    logger.LogInformation(
        "Order workflow status: {Status} | Shipped: {Shipped}",
        instance?.Status,
        System.Text.Json.JsonSerializer.Deserialize<OrderData>(instance?.DataJson ?? "{}")?.Shipped);
}

await host.StopAsync();
