using System.Text.RegularExpressions;

namespace SmartX.Shared.Sensors;

// A registered device. Covers the three fields the brief requires on the
// registration form: unique identifier, deployment location and category.
public sealed partial class SensorProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    // Device MAC address / unique identifier.
    public string MacAddress { get; set; } = string.Empty;

    // Friendly name shown on the Pulse Grid tile.
    public string DisplayName { get; set; } = string.Empty;

    // ----------------------------------------- deployment location
    public string Facility { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public string SubZone { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;

    public SensorCategory Category { get; set; } = SensorCategory.Environmental;
    public TelemetryUnit Unit { get; set; } = TelemetryUnit.Celsius;

    public bool RequiresMainsPower { get; set; }
    public DateTimeOffset RegisteredUtc { get; set; } = DateTimeOffset.UtcNow;

    // Config files, deployment photos and hardware logs.
    public List<SensorAttachment> Attachments { get; set; } = new();

    // Human-readable ancestry, e.g. "Facility A / Zone 1 / Sub-Zone B".
    public string LocationPath =>
        string.Join(" / ", new[] { Facility, Zone, SubZone }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    // Accepts AA:BB:CC:DD:EE:FF and AA-BB-CC-DD-EE-FF.
    [GeneratedRegex(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$")]
    private static partial Regex MacPattern();

    public static bool IsValidMac(string? mac) =>
        !string.IsNullOrWhiteSpace(mac) && MacPattern().IsMatch(mac.Trim());

    // Validates the profile and returns one message per problem. The same
    // method runs in the Blazor client for immediate feedback and again in
    // the API, so the browser never becomes the only line of defence.
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!IsValidMac(MacAddress))
            errors.Add("MAC address must look like AA:BB:CC:DD:EE:FF.");

        if (string.IsNullOrWhiteSpace(DisplayName))
            errors.Add("Display name is required.");

        if (string.IsNullOrWhiteSpace(Zone))
            errors.Add("Deployment zone is required.");

        if (string.IsNullOrWhiteSpace(NodeId))
            errors.Add("Node id is required.");

        if (!Enum.IsDefined(Category))
            errors.Add("Sensor category is not recognised.");

        if (Category == SensorCategory.Actuator && Unit != TelemetryUnit.Boolean)
            errors.Add("Actuators report a boolean state, so the unit must be Boolean.");

        if (Category == SensorCategory.PowerConsumption &&
            Unit is not (TelemetryUnit.Watt or TelemetryUnit.KiloWattHour))
            errors.Add("Power sensors must report in Watt or kWh.");

        return errors;
    }

    public bool IsValid() => Validate().Count == 0;
}

// Metadata for one uploaded config file, photo or hardware log.
public sealed class SensorAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedUtc { get; set; } = DateTimeOffset.UtcNow;

    // Path relative to the API's upload root. Never a client path.
    public string StoredPath { get; set; } = string.Empty;

    // SHA-256 of the content, for integrity checks on download.
    public string Sha256 { get; set; } = string.Empty;

    public string SizeDisplay => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):0.#} MB"
    };
}
