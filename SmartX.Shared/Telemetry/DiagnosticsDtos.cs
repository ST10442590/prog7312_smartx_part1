using SmartX.Shared.Sensors;

namespace SmartX.Shared.Telemetry;

/// <summary>Counters proving what happened to telemetry end to end.</summary>
public sealed class PipelineStatsDto
{
    /// <summary>Packets the API has accepted.</summary>
    public long Received { get; set; }

    /// <summary>Readings currently held in the historical archive.</summary>
    public long Stored { get; set; }

    /// <summary>Batches staged in the jagged buffer but not yet drained.</summary>
    public int StagedBatches { get; set; }

    /// <summary>Readings staged across those batches.</summary>
    public int StagedReadings { get; set; }

    /// <summary>Capacity of the backing List, showing growth behaviour.</summary>
    public int ArchiveCapacity { get; set; }

    /// <summary>Milliseconds the last drain into the List took.</summary>
    public double LastDrainMs { get; set; }

    /// <summary>Readings per second during the last drain.</summary>
    public double LastDrainThroughput { get; set; }

    public string? OldestUtc { get; set; }
    public string? NewestUtc { get; set; }
}

/// <summary>One row of the search result table.</summary>
public sealed class ReadingDto
{
    public string DeviceId { get; set; } = string.Empty;
    public double Value { get; set; }
    public TelemetryUnit Unit { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
}

/// <summary>Outcome of a timed search, including the algorithm used.</summary>
public sealed class SearchResultDto
{
    /// <summary>"Linear scan O(n)" or "Binary search O(log n)".</summary>
    public string Algorithm { get; set; } = string.Empty;

    public string Query { get; set; } = string.Empty;
    public int Comparisons { get; set; }
    public int RecordsSearched { get; set; }
    public int MatchCount { get; set; }
    public double ElapsedMs { get; set; }
    public List<ReadingDto> Sample { get; set; } = [];
}

/// <summary>One volume step of the performance harness.</summary>
public sealed class BenchmarkRowDto
{
    public int Records { get; set; }
    public double InsertMs { get; set; }
    public double LinearSearchMs { get; set; }
    public double BinarySearchMs { get; set; }

    /// <summary>Insertions per second at this volume.</summary>
    public double InsertThroughput { get; set; }
}

public sealed class BenchmarkResultDto
{
    public List<BenchmarkRowDto> Rows { get; set; } = [];
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Result of an aggregation performed with the overloaded operators on
/// <see cref="SensorReading"/>. The expression is carried back to the UI so
/// the demo can show the exact C# that produced the number.
/// </summary>
public sealed class AggregationDto
{
    /// <summary>The C# that ran, e.g. "meterA + meterB + meterC".</summary>
    public string Expression { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;
    public double Result { get; set; }
    public TelemetryUnit Unit { get; set; }

    /// <summary>Sensors folded into the result, tracked by SourceCount.</summary>
    public int SourceCount { get; set; }

    public double Mean { get; set; }
    public List<ReadingDto> Inputs { get; set; } = [];
    public string? Error { get; set; }
}
