using SmartX.Shared.Sensors;

namespace SmartX.Shared.Telemetry;

// Payload kinds a node may publish. The wire format carries this
// discriminator so the API can rebuild the correct
// <c>TelemetryPacket&lt;T&gt;</c> without guessing from the JSON.
public enum PayloadKind
{
    Float = 0,
    Integer = 1,
    Boolean = 2
}

// What an ESP32 posts to the ingestion endpoint. JSON cannot express an
// open generic, so the transport carries a discriminator plus a raw value,
// and the API closes the generic on arrival.
public sealed class TelemetryPacketDto
{
    public string DeviceId { get; set; } = string.Empty;
    public PayloadKind Kind { get; set; }

    // The reading. Booleans arrive as 1 or 0, which keeps the wire format
    // uniform and lets every channel share one severity scale.
    public double Value { get; set; }

    public SensorCategory Category { get; set; }
    public TelemetryUnit Unit { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset? CapturedAtUtc { get; set; }

    // Rebuilds the strongly-typed packet. The cast to the closed generic is
    // done once here, at the boundary, so nothing downstream handles a
    // loosely-typed value.
    public ITelemetryPacket ToPacket()
    {
        var stamp = CapturedAtUtc ?? DateTimeOffset.UtcNow;

        return Kind switch
        {
            PayloadKind.Float => new TelemetryPacket<float>(
                DeviceId, (float)Value, Category, Unit, stamp, Sequence),

            PayloadKind.Integer => new TelemetryPacket<int>(
                DeviceId, (int)Math.Round(Value), Category, Unit, stamp, Sequence),

            PayloadKind.Boolean => new TelemetryPacket<bool>(
                DeviceId, Value >= 0.5, Category, Unit, stamp, Sequence),

            _ => throw new ArgumentOutOfRangeException(
                nameof(Kind), Kind, "Unknown payload kind.")
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(DeviceId))
            errors.Add("Device id is required.");

        if (!Enum.IsDefined(Kind))
            errors.Add("Payload kind is not recognised.");

        if (double.IsNaN(Value) || double.IsInfinity(Value))
            errors.Add("Value must be a finite number.");

        if (Sequence < 0)
            errors.Add("Sequence cannot be negative.");

        if (CapturedAtUtc is { } t && t > DateTimeOffset.UtcNow.AddMinutes(5))
            errors.Add("Timestamp is too far in the future.");

        return errors;
    }
}

// One Pulse Grid tile, as pushed to the dashboard.
public sealed class DeviceHealthDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LocationPath { get; set; } = string.Empty;
    public SensorCategory Category { get; set; }
    public TelemetryUnit Unit { get; set; }

    public HealthState State { get; set; }
    public double ZScore { get; set; }
    public double LastValue { get; set; }
    public long PacketsSeen { get; set; }
    public double SilenceSeconds { get; set; }

    // Recent window, oldest-first, for the tile sparkline.
    public double[] Sparkline { get; set; } = Array.Empty<double>();

    // Plain-language state for the tile's ARIA live region, so the same
    // change reaches screen-reader users.
    public string AriaSummary => State switch
    {
        HealthState.Disconnected =>
            $"{DisplayName} disconnected, silent for {SilenceSeconds:0} seconds.",
        HealthState.Spike =>
            $"{DisplayName} spike, {Math.Abs(ZScore):0.0} deviations from baseline.",
        HealthState.Drift =>
            $"{DisplayName} drifting, {Math.Abs(ZScore):0.0} deviations from baseline.",
        _ => $"{DisplayName} nominal at {LastValue:0.##} {Unit}."
    };
}

// Mesh-wide counters for the dashboard header.
public sealed class MeshSummaryDto
{
    public int TotalDevices { get; set; }
    public int Nominal { get; set; }
    public int Drift { get; set; }
    public int Spike { get; set; }
    public int Disconnected { get; set; }
    public long TotalPackets { get; set; }
    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;

    // Share of the mesh currently healthy, 0 to 100.
    public double HealthPercent =>
        TotalDevices == 0 ? 100d : Math.Round(Nominal * 100d / TotalDevices, 1);
}
