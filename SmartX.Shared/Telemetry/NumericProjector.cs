using System.Runtime.CompilerServices;

namespace SmartX.Shared.Telemetry;

// Converts a generic value type <typeparamref name="T"/> to a double
// <b>without boxing</b>.
// <remarks>
// <para>
// The naive way to widen a generic value is <c>Convert.ToDouble(value)</c>
// or <c>(float)(object)value</c>. Both box: the struct is copied onto the
// managed heap, an object header is allocated, and the GC must later
// collect it. At Smart-X ingestion rates (thousands of packets per second)
// that is a per-packet heap allocation, which is exactly the
// "type-coercion overhead" the brief asks us to avoid.
// </para>
// <para>
// Instead, each closed generic type (NumericProjector&lt;float&gt;,
// NumericProjector&lt;int&gt;, ...) gets its own static field, resolved
// once by the runtime on first use. <see cref="Unsafe.As{TFrom,TTo}"/>
// then reinterprets the bits of the value in place — no allocation, no
// copy to the heap.
// </para>
// <para>
// The <c>typeof(T) == typeof(float)</c> comparisons are resolved at JIT
// time for value types, so the branches cost nothing at run time.
// </para>
// </remarks>
internal static class NumericProjector<T> where T : struct
{
    // Widens a <typeparamref name="T"/> to a double, allocation-free.
    public static readonly Func<T, double> ToDouble = Build();

    // True when <typeparamref name="T"/> is a type we can widen.
    public static readonly bool IsSupported = TryBuild() is not null;

    private static Func<T, double> Build()
    {
        return TryBuild() ?? throw new NotSupportedException(
            $"TelemetryPacket<{typeof(T).Name}> is not supported. " +
            "Supported payloads are float, double, int, long, short, byte, bool.");
    }

    private static Func<T, double>? TryBuild()
    {
        // Each branch reinterprets the value in place instead of boxing it.
        if (typeof(T) == typeof(float))
            return static v => Unsafe.As<T, float>(ref v);

        if (typeof(T) == typeof(double))
            return static v => Unsafe.As<T, double>(ref v);

        if (typeof(T) == typeof(int))
            return static v => Unsafe.As<T, int>(ref v);

        if (typeof(T) == typeof(long))
            return static v => Unsafe.As<T, long>(ref v);

        if (typeof(T) == typeof(short))
            return static v => Unsafe.As<T, short>(ref v);

        if (typeof(T) == typeof(byte))
            return static v => Unsafe.As<T, byte>(ref v);

        // A valve state is meaningful as 1 / 0 on the same severity scale
        // as every other channel, so the Pulse Grid can score it identically.
        if (typeof(T) == typeof(bool))
            return static v => Unsafe.As<T, bool>(ref v) ? 1d : 0d;

        return null;
    }
}
