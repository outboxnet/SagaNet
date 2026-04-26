using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SagaNet.Core.Abstractions;
using SagaNet.Extensions.DependencyInjection;
using SagaNet.Persistence.EfCore;
using SagaNet.Persistence.EfCore.Queries;
using SagaNet.Persistence.EfCore.Repositories;
using SagaNet.Sample.Workflows;
using SagaNet.Sample.Workflows.Steps;

// ──────────────────────────────────────────────────────────────────────────────
// Host / service registration
// ──────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Debug);

// ── Persistence (InMemory for zero-setup demo; swap for UseSqlServer in prod) ─
builder.Services.AddDbContext<SagaNetDbContext>(opt =>
    opt.UseInMemoryDatabase("SagaNetSample"));

builder.Services.AddScoped<IWorkflowRepository, EfWorkflowRepository>();

// ── Register step classes (DI injects ILogger etc. into them) ────────────────
builder.Services.AddTransient<ValidateOrderStep>();
builder.Services.AddTransient<ReserveInventoryStep>();
builder.Services.AddTransient<ProcessPaymentStep>();
builder.Services.AddTransient<ShipOrderStep>();

// ── SagaNet engine ────────────────────────────────────────────────────────────
builder.Services.AddSagaNet(b => b
    .AddWorkflow<OrderWorkflow, OrderData>()
    .Configure(opt =>
    {
        opt.PollInterval           = TimeSpan.FromMilliseconds(200);
        opt.PollBatchSize          = 5;
        opt.MaxConcurrentWorkflows = 4;
    }));

// ── Task progress query service ───────────────────────────────────────────────
builder.Services.AddSagaNetQueries<EfWorkflowQueryService>();

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddSagaNetHealthCheck();

var app = builder.Build();

// ── Ensure DB schema ──────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<SagaNetDbContext>().Database.EnsureCreatedAsync();

// ──────────────────────────────────────────────────────────────────────────────
// API endpoints
// ──────────────────────────────────────────────────────────────────────────────

// POST /orders  →  starts a new OrderWorkflow and returns its instanceId
app.MapPost("/orders", async (
    PlaceOrderRequest req,
    IWorkflowHost workflows,
    CancellationToken ct) =>
{
    var instanceId = await workflows.StartWorkflowAsync<OrderWorkflow, OrderData>(
        new OrderData
        {
            OrderId       = req.OrderId,
            Amount        = req.Amount,
            CustomerEmail = req.CustomerEmail
        }, ct);

    return Results.Accepted($"/orders/{instanceId}/progress",
        new { WorkflowInstanceId = instanceId });
});

// GET /orders/{instanceId}/progress  →  returns live task + step progress
app.MapGet("/orders/{instanceId:guid}/progress", async (
    Guid instanceId,
    IWorkflowQueryService queries,
    CancellationToken ct) =>
{
    var progress = await queries.GetProgressAsync(instanceId, ct);
    return progress is null
        ? Results.NotFound(new { error = "Workflow instance not found." })
        : Results.Ok(progress);
});

// GET /orders/recent  →  last 20 workflow instances (for admin / dashboard)
app.MapGet("/orders/recent", async (
    IWorkflowQueryService queries,
    CancellationToken ct) =>
{
    var recent = await queries.GetRecentAsync(limit: 20, ct);
    return Results.Ok(recent);
});

// POST /orders/{instanceId}/terminate  →  cancel a running workflow
app.MapPost("/orders/{instanceId:guid}/terminate", async (
    Guid instanceId,
    IWorkflowHost workflows,
    CancellationToken ct) =>
{
    await workflows.TerminateWorkflowAsync(instanceId, ct);
    return Results.Ok(new { message = "Workflow terminated." });
});

// GET /health
app.MapHealthChecks("/health");

app.Run();

// ──────────────────────────────────────────────────────────────────────────────
// Request DTO
// ──────────────────────────────────────────────────────────────────────────────

record PlaceOrderRequest(string OrderId, decimal Amount, string CustomerEmail);
