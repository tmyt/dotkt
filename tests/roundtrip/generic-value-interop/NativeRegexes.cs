using System.Text.RegularExpressions;

namespace RegexInterop;

public static class NativeRegexes
{
    public static Regex Sparse() => new("(?<10>a)(?<named>b)?");
    public static Regex Backwards(string pattern) => new(pattern, RegexOptions.RightToLeft);
}
