using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Simulates a mesh of ESP32 nodes publishing telemetry continuously.
/// </summary>
/// <remarks>
/// <para>The brief treats the physical mesh as a simulation, so this service
/// stands in for it: it registers a fictional deployment on startup and then
/// publishes readings on a timer for as long as the API runs.</para>
///
/// <para>Each node has its own baseline and noise level, so the dashboard
/// shows genuinely heterogeneous data rather than one repeated waveform.
/// Faults are injected at a low probability — a spike large enough to cross
/// the z-score threshold, or a temporary dropout — so the Pulse Grid can be
/// seen reacting live during a demo instead of sitting uniformly green.</para>
/// </remarks>
public sealed class MeshSimulator : BackgroundService
{
    private readonly DeviceRegistry _registry;
    private readonly ILogger<MeshSimulator> _logger;

    // Seeded so every run produces the same sequence. A demo that behaves
    // differently each time is impossible to narrate.
    private readonly Random _random = new(20260914);

    private readonly List<SimulatedNode> _nodes = new();

    /// <summary>How often the mesh publishes a round of telemetry.</summary>
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(800);

    /// <summary>Chance per node per round of an injected anomalous spike.</summary>
    private const double SpikeChance = 0.004;

    /// <summary>Chance per node per round of the node going silent.</summary>
    private const double DropoutChance = 0.002;

    public MeshSimulator(DeviceRegistry registry, ILogger<MeshSimulator> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>One simulated device and the shape of the signal it produces.</summary>
    private sealed class SimulatedNode
    {
        public required SensorProfile Profile { get; init; }
        public required PayloadKind Kind { get; init; }
        public required double Baseline { get; init; }
        public required double Noise { get; init; }
        public long Sequence { get; set; }

        /// <summary>Rounds remaining before this node starts publishing again.</summary>
        public int SilentRounds { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SeedMesh();

        _logger.LogInformation(
            "Mesh simulator started with {Count} nodes.", _nodes.Count);

        // PeriodicTimer does not drift the way a Task.Delay loop does, and it
        // cancels cleanly on shutdown.
        using var timer = new PeriodicTimer(PublishInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                PublishRound();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Mesh simulator stopped.");
    }

    /// <summary>Registers the fictional deployment.</summary>
    private void SeedMesh()
    {
        // Facility A -> Zone 1 / Zone 2 -> Sub-Zone A / Sub-Zone B
        var layout = new[]
        {
            ("Zone 1", "Sub-Zone A"),
            ("Zone 1", "Sub-Zone B"),
            ("Zone 2", "Sub-Zone A"),
            ("Zone 2", "Sub-Zone B")
        };

        var index = 0;

        foreach (var (zone, subZone) in layout)
        {
            // Three environmental, two power, one actuator per sub-zone:
            // twenty-four channels in total across the mesh.
            AddNode(index++, zone, subZone, SensorCategory.Environmental,
                TelemetryUnit.Celsius, PayloadKind.Float, baseline: 21.5, noise: 0.6);

            AddNode(index++, zone, subZone, SensorCategory.Environmental,
                TelemetryUnit.Percent, PayloadKind.Float, baseline: 62.0, noise: 2.4);

            AddNode(index++, zone, subZone, SensorCategory.Environmental,
                TelemetryUnit.Lux, PayloadKind.Float, baseline: 480.0, noise: 35.0);

            AddNode(index++, zone, subZone, SensorCategory.PowerConsumption,
                TelemetryUnit.Watt, PayloadKind.Integer, baseline: 1450, noise: 90);

            AddNode(index++, zone, subZone, SensorCategory.PowerConsumption,
                TelemetryUnit.KiloWattHour, PayloadKind.Float, baseline: 4.2, noise: 0.35);

            AddNode(index++, zone, subZone, SensorCategory.Actuator,
                TelemetryUnit.Boolean, PayloadKind.Boolean, baseline: 1, noise: 0);
        }
    }

    private void AddNode(
        int index, string zone, string subZone,
        SensorCategory category, TelemetryUnit unit, PayloadKind kind,
        double baseline, double noise)
    {
        var suffix = $"{(char)('A' + index % 26)}{index:00}";

        var profile = new SensorProfile
        {
            Id = $"ESP32-{suffix}",
            MacAddress = FormatMac(index),
            DisplayName = $"ESP32-{suffix}",
            Facility = "Facility A",
            Zone = zone,
            SubZone = subZone,
            NodeId = $"node-{index:000}",
            Category = category,
            Unit = unit,
            RequiresMainsPower = category == SensorCategory.Actuator
        };

        if (!_registry.TryRegister(profile, out var errors))
        {
            _logger.LogWarning(
                "Simulated node {Id} failed validation: {Errors}",
                profile.Id, string.Join("; ", errors));
            return;
        }

        _nodes.Add(new SimulatedNode
        {
            Profile = profile,
            Kind = kind,
            Baseline = baseline,
            Noise = noise
        });
    }

    private static string FormatMac(int index) =>
        $"5C:CF:7F:{index / 256 % 256:X2}:{index % 256:X2}:{(index * 7) % 256:X2}";

    /// <summary>Publishes one reading from every node that is not silent.</summary>
    private void PublishRound()
    {
        foreach (var node in _nodes)
        {
            // A node in a dropout window publishes nothing at all. The Pulse
            // Grid notices the absence rather than being told about it.
            if (node.SilentRounds > 0)
            {
                node.SilentRounds--;
                continue;
            }

            if (_random.NextDouble() < DropoutChance)
            {
                // Silent for roughly 40 to 90 seconds.
                node.SilentRounds = _random.Next(50, 110);
                _logger.LogInformation("Node {Id} dropped out.", node.Profile.Id);
                continue;
            }

            var value = NextValue(node);

            var dto = new TelemetryPacketDto
            {
                DeviceId = node.Profile.Id,
                Kind = node.Kind,
                Value = value,
                Category = node.Profile.Category,
                Unit = node.Profile.Unit,
                Sequence = ++node.Sequence,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };

            _registry.Observe(dto.ToPacket());
        }
    }

    private double NextValue(SimulatedNode node)
    {
        if (node.Kind == PayloadKind.Boolean)
        {
            // Valves mostly hold state, flipping occasionally.
            return _random.NextDouble() < 0.03 ? 0 : 1;
        }

        // Gaussian noise via Box-Muller, so the baseline looks like real
        // sensor scatter rather than a uniform band.
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        var gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) *
                       Math.Sin(2.0 * Math.PI * u2);

        var value = node.Baseline + gaussian * node.Noise;

        if (_random.NextDouble() < SpikeChance)
        {
            // Six to ten times the usual noise, comfortably past the spike
            // threshold, and signed so spikes go both ways.
            var direction = _random.NextDouble() < 0.5 ? -1 : 1;
            value += direction * node.Noise * _random.Next(6, 11);
            _logger.LogInformation("Injected spike on {Id}.", node.Profile.Id);
        }

        return value;
    }
}
