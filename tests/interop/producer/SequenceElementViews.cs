using System.Collections.Generic;

namespace SequenceViews;

public static class NativeSequences
{
    public static IEnumerable<int> Integers() { yield return 3; yield return 5; }
    public static IEnumerable<object> Mixed() { yield return "a"; yield return 7; yield return "b"; }
    public static int Sum(IEnumerable<int> values)
    {
        var result = 0;
        foreach (var value in values) result += value;
        return result;
    }
}
