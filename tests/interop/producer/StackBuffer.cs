using System;

namespace StackBufferInterop;

public static class SpanOperations
{
    public static int Sum(Span<int> values)
    {
        var total = 0;
        foreach (var value in values) total += value;
        return total;
    }

    public static void Fill(Span<int> values, int value)
    {
        for (var i = 0; i < values.Length; i++) values[i] = value;
    }
}
