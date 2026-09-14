using System.Globalization;
using SmartX.Shared.Sensors;

namespace SmartX.Shared.Telemetry;

// A single scalar reading, with arithmetic defined directly on it so that
// aggregation reads like the domain language rather than like plumbing:
// <code>
// SensorReading total = meter1 + meter2;   // combined load
// SensorReading drift = now - baseline;    // delta against baseline
// if (now > threshold) RaiseSpike();
// </code>
// <remarks>
// Declared as a <c>readonly struct</c>: readings are small, short-lived and
// created in enormous numbers, so keeping them on the stack avoids a heap
// allocation per reading. <c>readonly</c> also lets the compiler skip
// defensive copies when the struct is passed around.
// </remarks>
public readonly struct SensorReading : IEquatable<SensorReading>, IComparable<SensorReading>
{
    // Tolerance used by <see cref="ApproximatelyEquals"/>.
    public const double DefaultTolerance = 1e-9;

    public string DeviceId { get; }
    public double Value { get; }
    public TelemetryUnit Unit { get; }
    public DateTimeOffset TimestampUtc { get; }

    // How many physical readings this value represents. A reading straight
    // off a node is 1; the result of <c>meter1 + meter2</c> is 2. This is
    // what makes <see cref="Mean"/> correct after repeated aggregation.
    public int SourceCount { get; }

    public SensorReading(
        string deviceId,
        double value,
        TelemetryUnit unit,
        DateTimeOffset timestampUtc,
        int sourceCount = 1)
    {
        DeviceId = deviceId ?? string.Empty;
        Value = value;
        Unit = unit;
        TimestampUtc = timestampUtc;
        SourceCount = sourceCount < 1 ? 1 : sourceCount;
    }

    // Average per contributing sensor.
    public double Mean => Value / SourceCount;

    // True when this is the result of an aggregation.
    public bool IsComposite => SourceCount > 1;

    // ---------------------------------------------------------------- +
    // Aggregate load. <c>Meter3 = Meter1 + Meter2</c>.
    // <exception cref="InvalidOperationException">
    // Thrown when the units differ. Adding watts to degrees Celsius
    // produces a number that looks valid and means nothing, so the type
    // refuses rather than propagating a silent error through the dashboard.
    // </exception>
    public static SensorReading operator +(SensorReading left, SensorReading right)
    {
        GuardUnits(left, right, "+");

        return new SensorReading(
            deviceId: Combine(left.DeviceId, right.DeviceId),
            value: left.Value + right.Value,
            unit: left.Unit,
            // The aggregate is only as fresh as its stalest input.
            timestampUtc: left.TimestampUtc <= right.TimestampUtc
                ? left.TimestampUtc
                : right.TimestampUtc,
            sourceCount: left.SourceCount + right.SourceCount);
    }

    // ---------------------------------------------------------------- -
    // Delta comparison. <c>drift = current - baseline</c>. The result keeps
    // the left device's identity, since a delta belongs to the node being
    // measured, not to the baseline it was measured against.
    public static SensorReading operator -(SensorReading left, SensorReading right)
    {
        GuardUnits(left, right, "-");

        return new SensorReading(
            deviceId: left.DeviceId,
            value: left.Value - right.Value,
            unit: left.Unit,
            timestampUtc: left.TimestampUtc,
            sourceCount: 1);
    }

    // Unary negation, so a delta can be inverted directly.</summary>
    public static SensorReading operator -(SensorReading reading) =>
        new(reading.DeviceId, -reading.Value, reading.Unit,
            reading.TimestampUtc, reading.SourceCount);

    // ---------------------------------------------------------------- *
    // Scalar scaling, for unit conversion and calibration factors
    // (for example applying a current-transformer ratio to a meter).
    public static SensorReading operator *(SensorReading reading, double factor) =>
        new(reading.DeviceId, reading.Value * factor, reading.Unit,
            reading.TimestampUtc, reading.SourceCount);

    public static SensorReading operator *(double factor, SensorReading reading) =>
        reading * factor;

    // ------------------------------------------------------ comparisons
    // Magnitude comparisons, so threshold checks read naturally.
    // Units must match for the comparison to mean anything.

    public static bool operator >(SensorReading left, SensorReading right)
    {
        GuardUnits(left, right, ">");
        return left.Value > right.Value;
    }

    public static bool operator <(SensorReading left, SensorReading right)
    {
        GuardUnits(left, right, "<");
        return left.Value < right.Value;
    }

    public static bool operator >=(SensorReading left, SensorReading right)
    {
        GuardUnits(left, right, ">=");
        return left.Value >= right.Value;
    }

    public static bool operator <=(SensorReading left, SensorReading right)
    {
        GuardUnits(left, right, "<=");
        return left.Value <= right.Value;
    }

    // ------------------------------------------------------- equality
    // NOTE ON FLOATING POINT: == is exact structural equality, which keeps
    // it consistent with GetHashCode (two values that compare equal must
    // hash equal). For the fuzzy comparison you almost always want with
    // doubles, use ApproximatelyEquals instead.

    public static bool operator ==(SensorReading left, SensorReading right) =>
        left.Equals(right);

    public static bool operator !=(SensorReading left, SensorReading right) =>
        !left.Equals(right);

    public bool Equals(SensorReading other) =>
        string.Equals(DeviceId, other.DeviceId, StringComparison.OrdinalIgnoreCase)
        && Value.Equals(other.Value)
        && Unit == other.Unit
        && TimestampUtc == other.TimestampUtc
        && SourceCount == other.SourceCount;

    public override bool Equals(object? obj) =>
        obj is SensorReading other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            DeviceId?.ToLowerInvariant(),
            Value,
            Unit,
            TimestampUtc,
            SourceCount);

    // Value comparison within a tolerance, for float-safe checks.
    public bool ApproximatelyEquals(SensorReading other, double tolerance = DefaultTolerance) =>
        Unit == other.Unit && Math.Abs(Value - other.Value) <= tolerance;

    // Orders by magnitude, so a list of readings sorts sensibly.
    public int CompareTo(SensorReading other) => Value.CompareTo(other.Value);

    // ------------------------------------------------------ conversions
    // Explicit — not implicit — so a reading never silently decays into a
    // bare double and loses its unit.
  
    public static explicit operator double(SensorReading reading) => reading.Value;

    // ---------------------------------------------------------- helpers
    private static void GuardUnits(SensorReading left, SensorReading right, string op)
    {
        if (left.Unit != right.Unit)
        {
            throw new InvalidOperationException(
                $"Cannot apply '{op}' to readings with different units " +
                $"({left.Unit} and {right.Unit}). Convert one side first.");
        }
    }

    private static string Combine(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return right;
        if (string.IsNullOrEmpty(right)) return left;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            ? left
            : $"{left}+{right}";
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{DeviceId}: {Value:0.###} {Unit}{(IsComposite ? $" (n={SourceCount})" : "")}");
}
