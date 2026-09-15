using SmartX.Api.Services;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// The registry is a singleton because it holds live mesh state shared by
// every request and by the background simulator.
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddHostedService<MeshSimulator>();

builder.Services.AddOpenApi();

// The Blazor client is served from a different origin during development,
// so it needs an explicit CORS allowance. The policy is deliberately narrow
// rather than AllowAnyOrigin.
const string ClientCors = "SmartXClient";
builder.Services.AddCors(options =>
{
    options.AddPolicy(ClientCors, policy => policy
        .WithOrigins(
            "https://localhost:7296",
            "http://localhost:5006")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(ClientCors);

// ---------------------------------------------------------------- health
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "online",
    service = "Smart-X Ingestion Gateway",
    utc = DateTimeOffset.UtcNow
}))
.WithName("GetHealth")
.WithSummary("Liveness probe for the gateway.");

// ------------------------------------------------------------ ingestion
// The primary receiver of telemetry. Accepts one packet from a node.
app.MapPost("/api/telemetry", (TelemetryPacketDto dto, DeviceRegistry registry) =>
{
    var errors = dto.Validate();
    if (errors.Count > 0)
    {
        return Results.BadRequest(new { errors });
    }

    var score = registry.Observe(dto.ToPacket());

    // A packet from an unregistered node is a configuration error, not a
    // silent no-op, so it gets a 404 rather than a 200.
    return score is null
        ? Results.NotFound(new { error = $"Device '{dto.DeviceId}' is not registered." })
        : Results.Ok(new { dto.DeviceId, zScore = Math.Round(score.Value, 2) });
})
.WithName("IngestTelemetry")
.WithSummary("Receives a single telemetry packet from a sensor node.");

// Batch endpoint: a gateway forwarding a buffered window of packets.
app.MapPost("/api/telemetry/batch", (
    TelemetryPacketDto[] packets, DeviceRegistry registry) =>
{
    var accepted = 0;
    var rejected = new List<string>();

    foreach (var dto in packets)
    {
        if (dto.Validate().Count > 0 || registry.Observe(dto.ToPacket()) is null)
        {
            rejected.Add(dto.DeviceId);
            continue;
        }
        accepted++;
    }

    return Results.Ok(new { accepted, rejected });
})
.WithName("IngestTelemetryBatch")
.WithSummary("Receives a buffered batch of telemetry packets.");

// ------------------------------------------------------------- devices
app.MapGet("/api/devices", (DeviceRegistry registry) =>
    Results.Ok(registry.AllProfiles()))
.WithName("GetDevices")
.WithSummary("Lists every registered sensor profile.");

app.MapGet("/api/devices/{id}", (string id, DeviceRegistry registry) =>
{
    var profile = registry.GetProfile(id);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
})
.WithName("GetDevice")
.WithSummary("Returns one sensor profile.");

app.MapPost("/api/devices", (SensorProfile profile, DeviceRegistry registry) =>
{
    // Validation runs here as well as in the browser, so the client is never
    // the only thing standing between bad data and the mesh.
    if (!registry.TryRegister(profile, out var errors))
    {
        return Results.BadRequest(new { errors });
    }

    return Results.Created($"/api/devices/{profile.Id}", profile);
})
.WithName("RegisterDevice")
.WithSummary("Registers a new sensor, or updates an existing profile.");

// --------------------------------------------------------- pulse grid
app.MapGet("/api/mesh/health", (DeviceRegistry registry) =>
    Results.Ok(registry.Snapshot(DateTimeOffset.UtcNow)))
.WithName("GetMeshHealth")
.WithSummary("Current Pulse Grid state for every device, worst first.");

app.MapGet("/api/mesh/summary", (DeviceRegistry registry) =>
    Results.Ok(registry.Summarise(DateTimeOffset.UtcNow)))
.WithName("GetMeshSummary")
.WithSummary("Mesh-wide health counters.");

app.Run();