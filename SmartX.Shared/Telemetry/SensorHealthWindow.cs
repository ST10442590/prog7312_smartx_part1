using SmartX.Shared.Collections;
using SmartX.Shared.Sensors;

namespace SmartX.Shared.Telemetry;

// Maintains one device's rolling baseline and converts its latest reading
// into the severity score the Pulse Grid renders as colour.
// <remarks>
// Scoring is per device, not global. A greenhouse humidity sensor sitting
// at 85% and a server-room sensor sitting at 30% are both perfectly normal
// for where they are, so a single global threshold would either spam false
// alarms or miss real ones. A z-score — how many standard deviations the
// current reading sits from this node's own recent mean — puts every
// channel on one comparable scale.
// </remarks>
public sealed class SensorHealthWindow
{
    // Below this, the node is behaving normally.
    public const double DriftThreshold = 1.5;

    // At or above this, the reading is treated as a spike.
    public const double SpikeThreshold = 3.0;

    private readonly RingBuffer<double> _window;

    public string DeviceId { get; }
    public TelemetryUnit Unit { get; }

    // How long a node may stay silent before it counts as dropped.
    public TimeSpan HeartbeatTimeout { get; }

    public DateTimeOffset? LastPacketUtc { get; private set; }
    public double LastValue { get; private set; }
    public long PacketsSeen { get; private set; }

    public SensorHealthWindow(
        string deviceId,
        TelemetryUnit unit,
        int windowSize = 32,
        TimeSpan? heartbeatTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device id is required.", nameof(deviceId));

        DeviceId = deviceId;
        Unit = unit;
        _window = new RingBuffer<double>(windowSize);
        HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(30);
    }

    public int SampleCount => _window.Count;

    // Records a reading and returns the resulting severity score.
    public double Observe(in SensorReading reading)
    {
        // Score against the baseline as it stood *before* this reading, so a
        // spike is measured against history rather than partly against itself.
        var score = ZScoreFor(reading.Value);

        _window.Add(reading.Value);
        LastValue = reading.Value;
        LastPacketUtc = reading.TimestampUtc;
        PacketsSeen++;

        return score;
    }

    // Mean of the current window.
    public double Mean()
    {
        if (_window.Count == 0) return 0d;

        var sum = 0d;
        foreach (var value in _window) sum += value;
        return sum / _window.Count;
    }

    // Sample standard deviation of the current window, using the two-pass
    // method. The window is small and bounded, so the second pass is
    // cheap and avoids the cancellation error the naive
    // sum-of-squares shortcut suffers from.
    public double StandardDeviation()
    {
        if (_window.Count < 2) return 0d;

        var mean = Mean();
        var sumSquares = 0d;
        foreach (var value in _window)
        {
            var delta = value - mean;
            sumSquares += delta * delta;
        }
        return Math.Sqrt(sumSquares / (_window.Count - 1));
    }

    // How many standard deviations <paramref name="value"/> sits from the
    // window mean. Returns 0 while the baseline is still filling, so a node
    // that has only just come online does not alarm on its first packet.
    public double ZScoreFor(double value)
    {
        if (_window.Count < 4) return 0d;

        var sigma = StandardDeviation();

        // A flat baseline has no spread to measure against. Treat any change
        // as significant, but cap it so the tile does not saturate on noise.
        if (sigma < 1e-9)
        {
            return Math.Abs(value - Mean()) < 1e-9 ? 0d : SpikeThreshold;
        }

        return (value - Mean()) / sigma;
    }

    // The current z-score of the most recent reading.
    public double CurrentScore => ZScoreFor(LastValue);

    // How long this node has been silent.
    public TimeSpan SilenceFor(DateTimeOffset nowUtc) =>
        LastPacketUtc is null ? TimeSpan.Zero : nowUtc - LastPacketUtc.Value;

    // Maps the node onto a Pulse Grid band. Disconnection is checked first:
    // a silent node is a problem regardless of how healthy its last reading
    // happened to look.
    public HealthState Evaluate(DateTimeOffset nowUtc)
    {
        if (LastPacketUtc is null) return HealthState.Disconnected;
        if (SilenceFor(nowUtc) > HeartbeatTimeout) return HealthState.Disconnected;

        var magnitude = Math.Abs(CurrentScore);

        if (magnitude >= SpikeThreshold) return HealthState.Spike;
        if (magnitude >= DriftThreshold) return HealthState.Drift;
        return HealthState.Nominal;
    }

    // The window contents, oldest-first, for the tile sparkline.
    public double[] Sparkline() => _window.ToArray();

    public void Reset()
    {
        _window.Clear();
        LastPacketUtc = null;
        LastValue = 0d;
        PacketsSeen = 0;
    }
}