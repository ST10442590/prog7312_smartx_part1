using SmartX.Shared.Sensors;

namespace SmartX.Shared.Telemetry;

// The non-generic view of a packet.
// <remarks>
// This interface deliberately does <b>not</b> expose <c>object Value</c>.
// Doing so would box the payload the moment any heterogeneous collection
// read it, undoing the whole point of the generic design. Instead the
// shared surface exposes the already-widened
// <see cref="NumericValue"/> plus metadata, which is all the ingestion
// pipeline and the dashboard actually need.
// </remarks>
public interface ITelemetryPacket
{
    string DeviceId { get; }
    DateTimeOffset CapturedAtUtc { get; }
    SensorCategory Category { get; }
    TelemetryUnit Unit { get; }
    double NumericValue { get; }
    Type PayloadType { get; }
    string Describe();
}

// Reusable generic wrapper carrying one strongly-typed reading from one/// device: a float for soil moisture, an int for power wattage, a bool for
// a valve state — all handled uniformly, none of them boxed.
// <typeparam name="T">
// The payload type. Constrained to <c>struct</c> so the value is stored
// inline in the packet rather than as a heap reference.
// </typeparam>
public sealed class TelemetryPacket<T> : ITelemetryPacket where T : struct
{
    // The strongly-typed payload, stored inline. Never boxed.
    public T Value { get; }

    public string DeviceId { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public SensorCategory Category { get; }
    public TelemetryUnit Unit { get; }

    // Sequence number issued by the node, used to detect gaps.
    public long Sequence { get; }

    public TelemetryPacket(
        string deviceId,
        T value,
        SensorCategory category,
        TelemetryUnit unit,
        DateTimeOffset capturedAtUtc,
        long sequence = 0)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device id is required.", nameof(deviceId));

        DeviceId = deviceId;
        Value = value;
        Category = category;
        Unit = unit;
        CapturedAtUtc = capturedAtUtc;
        Sequence = sequence;
    }

    // The payload widened to a double so that heterogeneous packets can be
    // scored on one scale. Resolved through
    // <see cref="NumericProjector{T}"/>, so no boxing occurs.
    public double NumericValue => NumericProjector<T>.ToDouble(Value);

    public Type PayloadType => typeof(T);

    // Projects this packet onto the struct used for arithmetic.
    public SensorReading ToReading() =>
        new(DeviceId, NumericValue, Unit, CapturedAtUtc);

    public string Describe() =>
        $"{DeviceId} [{Category}] {Value} {Unit} @ {CapturedAtUtc:HH:mm:ss}";

    public override string ToString() => Describe();
}

// Factory helpers so callers can write
// <c>TelemetryPacket.Create("ESP32-A1", 21.4f, ...)</c> and let the
// compiler infer <c>T</c>, instead of spelling the type out every time.
public static class TelemetryPacket
{
    public static TelemetryPacket<T> Create<T>(
        string deviceId,
        T value,
        SensorCategory category,
        TelemetryUnit unit,
        DateTimeOffset? capturedAtUtc = null,
        long sequence = 0) where T : struct
        => new(deviceId, value, category, unit,
               capturedAtUtc ?? DateTimeOffset.UtcNow, sequence);

    // True when a payload type can be carried by a packet.
    public static bool IsSupportedPayload<T>() where T : struct
        => NumericProjector<T>.IsSupported;
}
