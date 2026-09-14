using System.Collections.Concurrent;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// In-memory store of every registered device, its profile and its rolling
/// health window.
/// </summary>
/// <remarks>
/// <para>Registered as a singleton, so it is shared across every request and
/// by the background seeder. That means concurrent access is guaranteed, not
/// hypothetical: ASP.NET Core serves requests on the thread pool while the
/// seeder publishes on its own timer. <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// handles the dictionary itself, and the per-device lock below protects the
/// health window, which is not thread-safe on its own.</para>
///
/// <para>State is deliberately in-memory rather than in a database: the brief
/// frames this as a simulated gateway, and telemetry windows are transient by
/// nature — only the last N readings matter, and they are cheap to rebuild.</para>
/// </remarks>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceEntry> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    private long _totalPackets;

    public int Count => _devices.Count;
    public long TotalPackets => Interlocked.Read(ref _totalPackets);

    /// <summary>One device: its registration profile plus its live window.</summary>
    private sealed class DeviceEntry
    {
        public required SensorProfile Profile { get; init; }
        public required SensorHealthWindow Window { get; init; }
        public Lock Gate { get; } = new();
    }

    /// <summary>
    /// Registers a device, or replaces the profile of one already present.
    /// Returns false with errors when the profile fails validation.
    /// </summary>
    public bool TryRegister(SensorProfile profile, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(profile);

        errors = profile.Validate();
        if (errors.Count > 0) return false;

        var entry = new DeviceEntry
        {
            Profile = profile,
            Window = new SensorHealthWindow(profile.Id, profile.Unit)
        };

        _devices.AddOrUpdate(
            profile.Id,
            entry,
            // Re-registering keeps the existing window, so updating a label
            // does not wipe the baseline this device has already built up.
            (_, existing) => new DeviceEntry
            {
                Profile = profile,
                Window = existing.Window
            });

        return true;
    }

    public bool Contains(string deviceId) => _devices.ContainsKey(deviceId);

    public SensorProfile? GetProfile(string deviceId) =>
        _devices.TryGetValue(deviceId, out var entry) ? entry.Profile : null;

    public IReadOnlyList<SensorProfile> AllProfiles() =>
        _devices.Values.Select(e => e.Profile).ToList();

    /// <summary>
    /// Records a packet against its device. Returns the resulting z-score,
    /// or null when the device is not registered — an unregistered node
    /// publishing telemetry is a configuration error worth surfacing rather
    /// than silently accepting.
    /// </summary>
    public double? Observe(ITelemetryPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!_devices.TryGetValue(packet.DeviceId, out var entry)) return null;

        var reading = new SensorReading(
            packet.DeviceId, packet.NumericValue, packet.Unit, packet.CapturedAtUtc);

        double score;
        lock (entry.Gate)
        {
            score = entry.Window.Observe(reading);
        }

        Interlocked.Increment(ref _totalPackets);
        return score;
    }

    /// <summary>Builds the current Pulse Grid, worst nodes first.</summary>
    public IReadOnlyList<DeviceHealthDto> Snapshot(DateTimeOffset nowUtc)
    {
        var tiles = new List<DeviceHealthDto>(_devices.Count);

        foreach (var entry in _devices.Values)
        {
            DeviceHealthDto tile;
            lock (entry.Gate)
            {
                tile = new DeviceHealthDto
                {
                    DeviceId = entry.Profile.Id,
                    DisplayName = entry.Profile.DisplayName,
                    LocationPath = entry.Profile.LocationPath,
                    Category = entry.Profile.Category,
                    Unit = entry.Profile.Unit,
                    State = entry.Window.Evaluate(nowUtc),
                    ZScore = Math.Round(entry.Window.CurrentScore, 2),
                    LastValue = Math.Round(entry.Window.LastValue, 3),
                    PacketsSeen = entry.Window.PacketsSeen,
                    SilenceSeconds = Math.Round(
                        entry.Window.SilenceFor(nowUtc).TotalSeconds, 1),
                    Sparkline = entry.Window.Sparkline()
                };
            }
            tiles.Add(tile);
        }

        // Worst first, then by deviation, so the operator's eye lands on the
        // problem before it lands on anything else.
        return tiles
            .OrderByDescending(t => t.State)
            .ThenByDescending(t => Math.Abs(t.ZScore))
            .ToList();
    }

    /// <summary>Mesh-wide counters for the dashboard header.</summary>
    public MeshSummaryDto Summarise(DateTimeOffset nowUtc)
    {
        var tiles = Snapshot(nowUtc);

        return new MeshSummaryDto
        {
            TotalDevices = tiles.Count,
            Nominal = tiles.Count(t => t.State == HealthState.Nominal),
            Drift = tiles.Count(t => t.State == HealthState.Drift),
            Spike = tiles.Count(t => t.State == HealthState.Spike),
            Disconnected = tiles.Count(t => t.State == HealthState.Disconnected),
            TotalPackets = TotalPackets,
            GeneratedUtc = nowUtc
        };
    }

    /// <summary>Attaches uploaded file metadata to a device profile.</summary>
    public bool TryAttach(string deviceId, SensorAttachment attachment)
    {
        if (!_devices.TryGetValue(deviceId, out var entry)) return false;

        lock (entry.Gate)
        {
            entry.Profile.Attachments.Add(attachment);
        }
        return true;
    }
}
