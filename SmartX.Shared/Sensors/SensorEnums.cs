namespace SmartX.Shared.Sensors;

// The three sensor families the Smart-X mesh supports. Matches the
// "Sensor Category Selection" requirement on the registration form.
public enum SensorCategory
{
    Environmental = 0,
    PowerConsumption = 1,
    Actuator = 2
}

// The unit a reading is expressed in. Aggregating two readings with
// different units is a programming error, so <see cref="SensorReading"/>
// refuses to do it rather than producing a silently wrong number.
public enum TelemetryUnit
{
    None = 0,
    Celsius = 1,
    Percent = 2,
    Watt = 3,
    KiloWattHour = 4,
    Lux = 5,
    Boolean = 6
}

// Severity band driving the Pulse Grid tile colour. The numeric order
// matters: the dashboard sorts descending so the worst node floats up.
public enum HealthState
{
    Nominal = 0,
    Drift = 1,
    Spike = 2,
    Disconnected = 3
}