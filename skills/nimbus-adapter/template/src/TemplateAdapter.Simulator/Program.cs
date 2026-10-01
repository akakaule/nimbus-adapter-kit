using NimBus.SDK;
using NimBus.SDK.Extensions;
using TemplateAdapter.Contracts.Endpoints;
using TemplateAdapter.Contracts.Upstream;
using TemplateAdapter.Simulator;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SimulatorState>();

// The upstream driver publishes sample events on the bus. It needs Service Bus, which the in-process
// tests do not have, so it is only wired when a connection is configured.
var hasServiceBus = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
if (hasServiceBus)
{
    builder.AddAzureServiceBusClient("servicebus");
    builder.Services.AddNimBusPublisher(nameof(UpstreamEndpoint));
}

var app = builder.Build();
app.MapDefaultEndpoints();

var requireToken = app.Configuration.GetValue("Simulator:RequireBearerToken", false);

// ---- The ExternalSystem API (what the adapter calls) ------------------------------------------
var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    var state = context.HttpContext.RequestServices.GetRequiredService<SimulatorState>();
    if (requireToken && !context.HttpContext.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
        return Error(StatusCodes.Status401Unauthorized, "Unauthorized", "A bearer token is required.");

    if (state.TakeFault() is { } fault)
    {
        if (fault.RetryAfterSeconds is int seconds)
            context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(fault.Status, fault.Status == StatusCodes.Status429TooManyRequests ? "TooManyRequests" : "ServiceUnavailable", "Injected by the simulator.");
    }

    return await next(context);
});

api.MapGet("/accounts/{accountKey}", (string accountKey, SimulatorState state) =>
    state.Accounts.TryGetValue(accountKey, out var account)
        ? Results.Ok(account)
        : Error(StatusCodes.Status404NotFound, "NotFound", $"Account '{accountKey}' does not exist."));

api.MapPut("/accounts/{accountKey}", (string accountKey, SimulatedAccount body, SimulatorState state) =>
{
    if (string.IsNullOrWhiteSpace(body.Name))
        return Error(StatusCodes.Status422UnprocessableEntity, "AccountNameRequired", "The account name is required.");
    if (!string.Equals(body.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
        return Error(StatusCodes.Status400BadRequest, "KeyMismatch", "The key in the body differs from the key in the URL.");
    if (state.Owned.ContainsKey(accountKey))
        return Error(StatusCodes.Status409Conflict, "OwnedByExternalSystem", $"Account '{accountKey}' is mastered in ExternalSystem.");
    if (body.ParentAccountKey is { } parent && !state.Accounts.ContainsKey(parent))
        return Error(StatusCodes.Status422UnprocessableEntity, "ParentNotFound", $"Parent account '{parent}' does not exist.");

    var created = state.Accounts.TryAdd(accountKey, body);
    if (!created)
        state.Accounts[accountKey] = body;

    return created ? Results.Created($"/api/accounts/{Uri.EscapeDataString(accountKey)}", body) : Results.Ok(body);
});

// ---- Driver endpoints (play the user and the outage) -------------------------------------------
var emulator = app.MapGroup("/emulator");

emulator.MapGet("/accounts", (SimulatorState state) => Results.Ok(state.Accounts.Values.OrderBy(a => a.AccountKey, StringComparer.Ordinal)));

emulator.MapPost("/accounts/{accountKey}/own", (string accountKey, SimulatorState state) =>
{
    state.Owned[accountKey] = true;
    return Results.NoContent();
});

// Example: POST /emulator/faults {"status":503,"durationSeconds":30} opens an "update window";
// {"status":429,"count":5,"retryAfterSeconds":2} throttles the next five calls.
emulator.MapPost("/faults", (FaultRequest request, SimulatorState state, TimeProvider clock) =>
{
    if (request.Status is not (429 or 500 or 502 or 503 or 504))
        return Results.BadRequest("status must be 429, 500, 502, 503 or 504.");

    state.PlanFault(new FaultPlan(
        request.Status,
        request.Count,
        request.DurationSeconds is int seconds ? clock.GetUtcNow().AddSeconds(seconds) : null,
        request.RetryAfterSeconds));
    return Results.NoContent();
});

emulator.MapDelete("/faults", (SimulatorState state) =>
{
    state.ClearFault();
    return Results.NoContent();
});

// Publishes an UpstreamAccountUpdated as the upstream system would: a deterministic MessageId
// (the same change published twice is deduplicated by the adapter's inbox) and the session key.
emulator.MapPost("/upstream/accounts", async (UpstreamAccountUpdated message, IServiceProvider services) =>
{
    if (services.GetService<IPublisherClient>() is not { } publisher)
        return Results.Problem("Service Bus is not configured (ConnectionStrings:servicebus).", statusCode: StatusCodes.Status503ServiceUnavailable);

    if (!message.TryValidate().IsValid)
        return Results.BadRequest("The account is invalid: AccountId and Name are required.");

    var messageId = $"account:{message.AccountId}:{message.ModifiedOn.UtcTicks}";
    await publisher.Publish(message, message.GetSessionId(), correlationId: messageId, messageId: messageId).ConfigureAwait(false);
    return Results.Accepted(value: new { messageId });
});

app.Run();

// The OData-style error body Dataverse, Business Central and F&O all use: {"error":{"code","message"}}.
static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = new { code, message } }, statusCode: status);
