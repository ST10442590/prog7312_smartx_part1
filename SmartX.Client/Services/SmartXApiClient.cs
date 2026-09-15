using System.Net.Http.Headers;
using System.Net.Http.Json;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Client.Services;

/// <summary>
/// Every call the dashboard makes to the Smart-X gateway goes through here.
/// </summary>
/// <remarks>
/// Centralising the HTTP surface keeps the Razor components free of URL
/// strings and serialisation detail, and means a change to a route is a
/// one-line edit rather than a hunt through the UI. Each method returns a
/// result the caller can act on rather than throwing, because a dashboard
/// that blanks out when the gateway restarts is worse than one that says
/// the gateway is unreachable.
/// </remarks>
public sealed class SmartXApiClient
{
    private readonly HttpClient _http;

    public SmartXApiClient(HttpClient http) => _http = http;

    /// <summary>True when the gateway responds to a liveness probe.</summary>
    public async Task<bool> IsOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ------------------------------------------------------- pulse grid
    /// <summary>Current Pulse Grid tiles, worst devices first.</summary>
    public async Task<IReadOnlyList<DeviceHealthDto>> GetMeshHealthAsync(
        CancellationToken ct = default)
    {
        try
        {
            var tiles = await _http.GetFromJsonAsync<List<DeviceHealthDto>>(
                "/api/mesh/health", ct);
            return tiles ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A dropped poll is not worth tearing the UI down for; the next
            // tick will pick it up.
            return [];
        }
    }

    /// <summary>Mesh-wide counters for the dashboard header.</summary>
    public async Task<MeshSummaryDto?> GetMeshSummaryAsync(
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<MeshSummaryDto>(
                "/api/mesh/summary", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------- devices
    public async Task<IReadOnlyList<SensorProfile>> GetDevicesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var profiles = await _http.GetFromJsonAsync<List<SensorProfile>>(
                "/api/devices", ct);
            return profiles ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    public async Task<SensorProfile?> GetDeviceAsync(
        string deviceId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SensorProfile>(
                $"/api/devices/{deviceId}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Registers a sensor. Returns the API's validation errors when the
    /// server rejects it, so the form can show server-side failures the
    /// browser did not catch.
    /// </summary>
    public async Task<ApiResult> RegisterDeviceAsync(
        SensorProfile profile, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/devices", profile, ct);

            if (response.IsSuccessStatusCode) return ApiResult.Ok();

            var payload = await response.Content
                .ReadFromJsonAsync<ErrorPayload>(cancellationToken: ct);

            return ApiResult.Fail(payload?.Errors ?? ["The gateway rejected the registration."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult.Fail(["Could not reach the gateway. Is the API running?"]);
        }
    }

    // -------------------------------------------------------- telemetry
    /// <summary>Publishes a single packet, used by the manual test panel.</summary>
    public async Task<ApiResult> SendTelemetryAsync(
        TelemetryPacketDto packet, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/telemetry", packet, ct);

            if (response.IsSuccessStatusCode) return ApiResult.Ok();

            var payload = await response.Content
                .ReadFromJsonAsync<ErrorPayload>(cancellationToken: ct);

            return ApiResult.Fail(payload?.Errors ?? ["The gateway rejected the packet."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult.Fail(["Could not reach the gateway."]);
        }
    }

    // ------------------------------------------------------- attachments
    /// <summary>
    /// Uploads a config file, deployment photo or hardware log against a
    /// sensor profile, streaming the content rather than buffering it into
    /// a byte array first.
    /// </summary>
    public async Task<ApiResult> UploadAttachmentAsync(
        string deviceId,
        Stream content,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            using var streamContent = new StreamContent(content);

            streamContent.Headers.ContentType =
                new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType)
                    ? "application/octet-stream"
                    : contentType);

            form.Add(streamContent, "file", fileName);

            using var response = await _http.PostAsync(
                $"/api/devices/{deviceId}/attachments", form, ct);

            if (response.IsSuccessStatusCode) return ApiResult.Ok();

            var payload = await response.Content
                .ReadFromJsonAsync<ErrorPayload>(cancellationToken: ct);

            return ApiResult.Fail(payload?.Errors ?? ["The upload was rejected."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult.Fail(["Could not reach the gateway during upload."]);
        }
    }

    private sealed class ErrorPayload
    {
        public List<string>? Errors { get; set; }
    }
}

/// <summary>
/// Outcome of a call that can fail for reasons the user needs to see.
/// </summary>
public sealed class ApiResult
{
    public bool Succeeded { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static ApiResult Ok() => new() { Succeeded = true };

    public static ApiResult Fail(IReadOnlyList<string> errors) =>
        new() { Succeeded = false, Errors = errors };
}