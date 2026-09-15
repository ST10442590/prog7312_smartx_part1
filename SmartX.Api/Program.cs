using SmartX.Api.Services;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// The registry is a singleton because it holds live mesh state shared by
// every request and by the background simulator.
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddScoped<TopologyBuilder>();
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

// ------------------------------------------------------- attachments
// Config files, deployment photos and hardware logs, attached to a sensor
// profile. The stream is copied straight to disk rather than buffered into
// memory, so a large hardware log does not cost a matching allocation on
// the gateway.
app.MapPost("/api/devices/{id}/attachments", async (
    string id,
    IFormFile file,
    DeviceRegistry registry,
    IWebHostEnvironment env) =>
{
    if (registry.GetProfile(id) is null)
    {
        return Results.NotFound(new { errors = new[] { $"Device '{id}' is not registered." } });
    }

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { errors = new[] { "No file was supplied." } });
    }

    const long maxBytes = 25 * 1024 * 1024;
    if (file.Length > maxBytes)
    {
        return Results.BadRequest(new { errors = new[] { "Files are limited to 25 MB." } });
    }

    // Only the extensions a device profile legitimately needs.
    string[] allowed = [".json", ".yaml", ".yml", ".txt", ".log", ".csv",
                        ".cfg", ".ini", ".png", ".jpg", ".jpeg", ".webp"];

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!allowed.Contains(extension))
    {
        return Results.BadRequest(new
        {
            errors = new[] { $"'{extension}' files are not accepted." }
        });
    }

    var root = Path.Combine(env.ContentRootPath, "uploads", id);
    Directory.CreateDirectory(root);

    // The stored name is generated, never taken from the client. A filename
    // like "../../appsettings.json" would otherwise escape the upload folder.
    var storedName = $"{Guid.NewGuid():N}{extension}";
    var storedPath = Path.Combine(root, storedName);

    string hash;
    await using (var destination = File.Create(storedPath))
    {
        await using var source = file.OpenReadStream();
        await source.CopyToAsync(destination);

        destination.Position = 0;
        using var sha = System.Security.Cryptography.SHA256.Create();
        hash = Convert.ToHexString(await sha.ComputeHashAsync(destination));
    }

    var attachment = new SensorAttachment
    {
        FileName = Path.GetFileName(file.FileName),
        ContentType = file.ContentType,
        SizeBytes = file.Length,
        StoredPath = Path.Combine("uploads", id, storedName),
        Sha256 = hash
    };

    return registry.TryAttach(id, attachment)
        ? Results.Ok(attachment)
        : Results.NotFound(new { errors = new[] { $"Device '{id}' is not registered." } });
})
.WithName("UploadAttachment")
.WithSummary("Attaches a config file, photo or hardware log to a sensor profile.")
.DisableAntiforgery();

app.MapGet("/api/devices/{id}/attachments", (string id, DeviceRegistry registry) =>
{
    var profile = registry.GetProfile(id);
    return profile is null
        ? Results.NotFound()
        : Results.Ok(profile.Attachments);
})
.WithName("GetAttachments")
.WithSummary("Lists files attached to a sensor profile.");

// --------------------------------------------------------- topology
// Exercises the recursive validator over the live mesh: tier ordering,
// mains-power ancestry, cycles and depth are all checked by walking the
// tree rather than by inspecting the flat device list.
app.MapGet("/api/topology/validate", (TopologyBuilder topology) =>
{
    var report = topology.Validate();

    return Results.Ok(new
    {
        report.IsValid,
        report.ErrorCount,
        report.WarningCount,
        report.NodesVisited,
        report.MaxDepthReached,
        Issues = report.Issues
    });
})
.WithName("ValidateTopology")
.WithSummary("Recursively validates the deployment tree.");

app.MapGet("/api/topology/tree", (TopologyBuilder topology) =>
    Results.Ok(topology.Build()))
.WithName("GetTopologyTree")
.WithSummary("Returns the deployment tree assembled from registered devices.");

app.MapGet("/api/topology/path/{deviceId}", (
    string deviceId, TopologyBuilder topology) =>
{
    var path = topology.PathTo(deviceId);
    return path is null
        ? Results.NotFound(new { error = $"'{deviceId}' is not in the tree." })
        : Results.Ok(new { deviceId, path });
})
.WithName("GetDevicePath")
.WithSummary("Recursively resolves a device's ancestry path.");

app.Run();