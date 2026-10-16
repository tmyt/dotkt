using System.Collections.Generic;

namespace AuthoredMapRoundtrip;

public static class NativeDictionary
{
    public static int Count(object source) => ((IDictionary<int, string>)source).Count;
    public static string Get(object source, int key) => ((IDictionary<int, string>)source)[key];
}
