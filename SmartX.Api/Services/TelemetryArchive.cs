using System.Diagnostics;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Collections;

namespace SmartX.Api.Services;

/// <summary>
/// The historical store. Incoming batches are staged in a jagged array and
/// then transferred, in one pass, into an optimised <see cref="List{T}"/>.
/// </summary>
/// <remarks>
/// <para>This is where the data-structure story ends: <see cref="RingBuffer{T}"/>
/// keeps the last N readings per device for the live Pulse Grid, while this
/// class keeps the full history for search and aggregation. The two have
/// different jobs, which is why they are different structures.</para>
///
/// <para>The archive is capped. An unbounded history would eventually exhaust
/// memory, and for a gateway the recent past is what matters — older data
/// belongs in cold storage, not in the ingestion process.</para>
/// </remarks>
public sealed class TelemetryArchive
{
    /// <summary>Readings retained before the oldest are trimmed.</summary>
    public const int MaxReadings = 500_000;

    // Time-ordered because packets arrive in time order. That ordering is
    // what makes the binary search below valid.
    private readonly List<SensorReading> _readings = new(capacity: 8192);

    private readonly TelemetryBatchBuffer _buffer = new(batchCapacity: 32, zoneCount: 8);
    private readonly Lock _gate = new();

    private long _received;
    private double _lastDrainMs;
    private double _lastDrainThroughput;

    /// <summary>
    /// Receives a transmitted batch. Packets are staged in the jagged buffer
    /// first; when it fills, the whole staging area is drained into the List
    /// in a single pass rather than element by element.
    /// </summary>
    public int Ingest(IReadOnlyList<ITelemetryPacket> packets, TelemetryUnit unit)
    {
        if (packets.Count == 0) return 0;

        lock (_gate)
        {
            _received += packets.Count;

            // A full buffer must be drained before it can take more.
            if (!_buffer.TryAddBatch(packets))
            {
                Drain();
                _buffer.TryAddBatch(packets);
            }

            return packets.Count;
        }
    }

    /// <summary>Forces a drain, so the demo can show the transfer on demand.</summary>
    public int Flush()
    {
        lock (_gate) return Drain();
    }

    /// <summary>
    /// Moves everything staged in the jagged array into the List. Timed, so
    /// the throughput can be shown as the volume grows.
    /// </summary>
    private int Drain()
    {
        var staged = _buffer.StagedCount();
        if (staged == 0) return 0;

        var sw = Stopwatch.StartNew();
        var moved = _buffer.DrainTo(_readings, TelemetryUnit.None);
        sw.Stop();

        _lastDrainMs = sw.Elapsed.TotalMilliseconds;
        _lastDrainThroughput = _lastDrainMs > 0
            ? moved / (_lastDrainMs / 1000.0)
            : 0;

        // Trim the oldest once the cap is passed. RemoveRange from the front
        // is O(n), so it is done in large blocks rather than one at a time.
        if (_readings.Count > MaxReadings)
        {
            _readings.RemoveRange(0, _readings.Count - MaxReadings);
        }

        return moved;
    }

    public PipelineStatsDto Stats()
    {
        lock (_gate)
        {
            return new PipelineStatsDto
            {
                Received = _received,
                Stored = _readings.Count,
                StagedBatches = _buffer.BatchCount,
                StagedReadings = _buffer.StagedCount(),
                ArchiveCapacity = _readings.Capacity,
                LastDrainMs = Math.Round(_lastDrainMs, 3),
                LastDrainThroughput = Math.Round(_lastDrainThroughput, 0),
                OldestUtc = _readings.Count > 0
                    ? _readings[0].TimestampUtc.ToString("HH:mm:ss.fff") : null,
                NewestUtc = _readings.Count > 0
                    ? _readings[^1].TimestampUtc.ToString("HH:mm:ss.fff") : null
            };
        }
    }

    // ------------------------------------------------------------ search
    /// <summary>
    /// Linear scan over the whole archive, matching device and value range.
    /// O(n): every record is examined, so the cost grows in step with volume.
    /// </summary>
    public SearchResultDto SearchByValue(string? deviceId, double min, double max, int take = 20)
    {
        lock (_gate)
        {
            var sw = Stopwatch.StartNew();
            var matches = new List<SensorReading>();
            var comparisons = 0;

            foreach (var reading in _readings)
            {
                comparisons++;

                if (!string.IsNullOrWhiteSpace(deviceId) &&
                    !string.Equals(reading.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (reading.Value >= min && reading.Value <= max)
                    matches.Add(reading);
            }

            sw.Stop();

            return new SearchResultDto
            {
                Algorithm = "Linear scan — O(n)",
                Query = $"{deviceId ?? "any device"}, value between {min:0.##} and {max:0.##}",
                Comparisons = comparisons,
                RecordsSearched = _readings.Count,
                MatchCount = matches.Count,
                ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 4),
                Sample = matches.TakeLast(take).Select(Project).ToList()
            };
        }
    }

    /// <summary>
    /// Binary search for the first reading at or after a timestamp.
    /// O(log n), valid only because the archive is time-ordered by
    /// construction — which is the reason ordering is preserved on insert.
    /// </summary>
    public SearchResultDto SearchByTime(DateTimeOffset from, int take = 20)
    {
        lock (_gate)
        {
            var sw = Stopwatch.StartNew();

            var low = 0;
            var high = _readings.Count - 1;
            var found = _readings.Count;
            var comparisons = 0;

            while (low <= high)
            {
                comparisons++;
                var mid = low + (high - low) / 2;

                if (_readings[mid].TimestampUtc >= from)
                {
                    found = mid;
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            var matched = _readings.Count - found;
            var sample = new List<ReadingDto>();

            for (var i = found; i < _readings.Count && sample.Count < take; i++)
            {
                sample.Add(Project(_readings[i]));
            }

            sw.Stop();

            return new SearchResultDto
            {
                Algorithm = "Binary search — O(log n)",
                Query = $"first reading at or after {from:HH:mm:ss}",
                Comparisons = comparisons,
                RecordsSearched = _readings.Count,
                MatchCount = matched,
                ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 4),
                Sample = sample
            };
        }
    }

    // ------------------------------------------------------- aggregation
    /// <summary>
    /// Aggregate load across several meters, using the overloaded <c>+</c>
    /// on <see cref="SensorReading"/>. This is the <c>Meter3 = Meter1 +
    /// Meter2</c> case from the brief, folded across any number of sensors.
    /// </summary>
    public AggregationDto AggregateLoad(IReadOnlyList<string> deviceIds)
    {
        lock (_gate)
        {
            var latest = new List<SensorReading>();

            foreach (var id in deviceIds)
            {
                for (var i = _readings.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_readings[i].DeviceId, id,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        latest.Add(_readings[i]);
                        break;
                    }
                }
            }

            if (latest.Count < 2)
            {
                return new AggregationDto
                {
                    Operation = "Aggregate (+)",
                    Error = "Select at least two sensors that have reported readings."
                };
            }

            try
            {
                // The overloaded operator doing the work.
                var total = latest[0];
                for (var i = 1; i < latest.Count; i++)
                {
                    total = total + latest[i];
                }

                return new AggregationDto
                {
                    Operation = "Aggregate (+)",
                    Expression = string.Join(" + ", latest.Select(r => r.DeviceId)),
                    Result = Math.Round(total.Value, 3),
                    Unit = total.Unit,
                    SourceCount = total.SourceCount,
                    Mean = Math.Round(total.Mean, 3),
                    Inputs = latest.Select(Project).ToList()
                };
            }
            catch (InvalidOperationException ex)
            {
                // The operator refuses to add mismatched units rather than
                // returning a plausible but meaningless number.
                return new AggregationDto
                {
                    Operation = "Aggregate (+)",
                    Error = ex.Message,
                    Inputs = latest.Select(Project).ToList()
                };
            }
        }
    }

    /// <summary>
    /// Delta between two sensors' latest readings, using the overloaded
    /// <c>-</c>, plus a magnitude comparison using the overloaded <c>&gt;</c>.
    /// </summary>
    public AggregationDto Delta(string leftId, string rightId)
    {
        lock (_gate)
        {
            var left = Latest(leftId);
            var right = Latest(rightId);

            if (left is null || right is null)
            {
                return new AggregationDto
                {
                    Operation = "Delta (-)",
                    Error = "Both sensors must have reported at least one reading."
                };
            }

            try
            {
                var delta = left.Value - right.Value;          // operator -
                var leftIsHigher = left.Value > right.Value;   // operator >

                return new AggregationDto
                {
                    Operation = "Delta (-)",
                    Expression = $"{leftId} - {rightId}   |   {leftId} > {rightId} is {leftIsHigher}",
                    Result = Math.Round(delta.Value, 3),
                    Unit = delta.Unit,
                    SourceCount = delta.SourceCount,
                    Mean = Math.Round(delta.Mean, 3),
                    Inputs = [Project(left.Value), Project(right.Value)]
                };
            }
            catch (InvalidOperationException ex)
            {
                return new AggregationDto
                {
                    Operation = "Delta (-)",
                    Error = ex.Message,
                    Inputs = [Project(left.Value), Project(right.Value)]
                };
            }
        }
    }

    private SensorReading? Latest(string deviceId)
    {
        for (var i = _readings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_readings[i].DeviceId, deviceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _readings[i];
            }
        }
        return null;
    }

    // -------------------------------------------------------- benchmark
    /// <summary>
    /// Measures insertion and search against growing volumes, on a private
    /// archive so the live one is untouched. This is the evidence for how
    /// the structures behave as telemetry size increases.
    /// </summary>
    public static BenchmarkResultDto Benchmark(int[] volumes)
    {
        var result = new BenchmarkResultDto
        {
            Notes = "Insertion stages batches in a jagged double[][] and drains " +
                    "into a List<SensorReading> pre-sized to the exact capacity. " +
                    "Linear search scans every record; binary search relies on " +
                    "the archive being time-ordered."
        };

        foreach (var volume in volumes)
        {
            var readings = new List<SensorReading>(volume);
            var buffer = new TelemetryBatchBuffer(batchCapacity: 64, zoneCount: 8);
            var start = DateTimeOffset.UtcNow;

            var sw = Stopwatch.StartNew();

            const int batchSize = 500;
            var packets = new List<ITelemetryPacket>(batchSize);

            for (var i = 0; i < volume; i++)
            {
                packets.Add(new TelemetryPacket<float>(
                    $"BENCH-{i % 24:00}",
                    20f + i % 17,
                    SensorCategory.Environmental,
                    TelemetryUnit.Celsius,
                    start.AddMilliseconds(i)));

                if (packets.Count == batchSize)
                {
                    if (!buffer.TryAddBatch(packets))
                    {
                        buffer.DrainTo(readings, TelemetryUnit.Celsius);
                        buffer.TryAddBatch(packets);
                    }
                    packets.Clear();
                }
            }

            if (packets.Count > 0) buffer.TryAddBatch(packets);
            buffer.DrainTo(readings, TelemetryUnit.Celsius);

            sw.Stop();
            var insertMs = sw.Elapsed.TotalMilliseconds;

            // Linear: touches every record.
            sw.Restart();
            var hits = 0;
            foreach (var reading in readings)
            {
                if (reading.Value >= 30 && reading.Value <= 32) hits++;
            }
            sw.Stop();
            var linearMs = sw.Elapsed.TotalMilliseconds;

            // Binary: halves the range each comparison.
            var target = start.AddMilliseconds(volume * 0.75);
            sw.Restart();
            var low = 0;
            var high = readings.Count - 1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                if (readings[mid].TimestampUtc >= target) high = mid - 1;
                else low = mid + 1;
            }
            sw.Stop();

            result.Rows.Add(new BenchmarkRowDto
            {
                Records = volume,
                InsertMs = Math.Round(insertMs, 3),
                LinearSearchMs = Math.Round(linearMs, 4),
                BinarySearchMs = Math.Round(sw.Elapsed.TotalMilliseconds, 4),
                InsertThroughput = insertMs > 0
                    ? Math.Round(volume / (insertMs / 1000.0), 0) : 0
            });
        }

        return result;
    }

    private static ReadingDto Project(SensorReading reading) => new()
    {
        DeviceId = reading.DeviceId,
        Value = Math.Round(reading.Value, 3),
        Unit = reading.Unit,
        TimestampUtc = reading.TimestampUtc
    };
}