
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Shared.Collections;

// Stages raw telemetry in a <b>jagged array</b> of sequential batches
// before draining it into an optimised <see cref="List{T}"/>.
// <remarks>
// <para><b>Why jagged and not rectangular.</b> Each ingestion window
// delivers a different number of packets — a hydroponic zone might send 12
// readings while a smart-grid zone sends 4,000. A rectangular
// <c>double[batches, readings]</c> would have to size every row to the
// largest one, wasting memory on every short batch. A jagged
// <c>double[][]</c> allocates each row to its true length, so the memory
// footprint tracks the data rather than the worst case.</para>
// <para><b>Why stage at all.</b> Appending straight into a List per packet
// causes repeated growth and copying under load. Collecting into a fixed
// raw array first, then transferring once into a List created at exactly
// the right capacity, means the List never reallocates during the drain.</para>
// </remarks>
public sealed class TelemetryBatchBuffer
{
    private readonly double[][] _batches;
    private readonly string[][] _deviceIds;
    private readonly DateTimeOffset[] _batchStamps;

    // Rectangular tally of packet counts, indexed
    // [zoneIndex, categoryIndex]. Genuinely rectangular data — every zone
    // has exactly the same three category slots — so a 2-D array is the
    // right shape here, and it keeps the counters in one contiguous block.
    private readonly int[,] _zoneCategoryCounts;

    private int _batchCursor;

    public int Capacity => _batches.Length;
    public int BatchCount => _batchCursor;
    public int ZoneCount { get; }

    public TelemetryBatchBuffer(int batchCapacity = 64, int zoneCount = 8)
    {
        if (batchCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(batchCapacity));
        if (zoneCount < 1)
            throw new ArgumentOutOfRangeException(nameof(zoneCount));

        _batches = new double[batchCapacity][];
        _deviceIds = new string[batchCapacity][];
        _batchStamps = new DateTimeOffset[batchCapacity];

        ZoneCount = zoneCount;
        var categories = Enum.GetValues<SensorCategory>().Length;
        _zoneCategoryCounts = new int[zoneCount, categories];
    }

    // Appends one historical batch. Returns false when the buffer is full,
    // signalling the caller to <see cref="DrainTo"/> first.
    public bool TryAddBatch(IReadOnlyList<ITelemetryPacket> packets, int zoneIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(packets);
        if (_batchCursor >= _batches.Length) return false;

        // Each jagged row is sized to this batch exactly — no padding.
        var values = new double[packets.Count];
        var ids = new string[packets.Count];

        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];
            values[i] = packet.NumericValue;
            ids[i] = packet.DeviceId;

            if (zoneIndex >= 0 && zoneIndex < ZoneCount)
            {
                _zoneCategoryCounts[zoneIndex, (int)packet.Category]++;
            }
        }

        _batches[_batchCursor] = values;
        _deviceIds[_batchCursor] = ids;
        _batchStamps[_batchCursor] = DateTimeOffset.UtcNow;
        _batchCursor++;
        return true;
    }

    // Total packets currently staged across every batch.
    public int StagedCount()
    {
        var total = 0;
        for (var i = 0; i < _batchCursor; i++)
        {
            total += _batches[i]?.Length ?? 0;
        }
        return total;
    }

    // Transfers everything staged into the supplied list and resets the
    // buffer. The list is grown once, up front, to the exact size needed,
    // so the transfer performs no intermediate reallocations.
    public int DrainTo(List<SensorReading> destination, TelemetryUnit unit)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var required = destination.Count + StagedCount();
        if (destination.Capacity < required) destination.Capacity = required;

        var moved = 0;
        for (var b = 0; b < _batchCursor; b++)
        {
            var values = _batches[b];
            var ids = _deviceIds[b];
            if (values is null || ids is null) continue;

            var stamp = _batchStamps[b];
            for (var i = 0; i < values.Length; i++)
            {
                destination.Add(new SensorReading(ids[i], values[i], unit, stamp));
                moved++;
            }

            // Drop the row reference so the GC can reclaim it immediately
            // rather than at the next full drain.
            _batches[b] = null!;
            _deviceIds[b] = null!;
        }

        _batchCursor = 0;
        return moved;
    }

    // Packet count for one zone and category, from the 2-D tally.
    public int CountFor(int zoneIndex, SensorCategory category)
    {
        if (zoneIndex < 0 || zoneIndex >= ZoneCount) return 0;
        return _zoneCategoryCounts[zoneIndex, (int)category];
    }

    // Row totals across all categories, for the dashboard header.
    public int[] ZoneTotals()
    {
        var categories = _zoneCategoryCounts.GetLength(1);
        var totals = new int[ZoneCount];

        for (var z = 0; z < ZoneCount; z++)
        {
            var sum = 0;
            for (var c = 0; c < categories; c++) sum += _zoneCategoryCounts[z, c];
            totals[z] = sum;
        }
        return totals;
    }

    public void ResetCounters() => Array.Clear(_zoneCategoryCounts);
}