using System.Text.RegularExpressions;

namespace RegexInterop;

public static class NativeRegexes
{
    private sealed class AlternatePattern() : Regex("(a)")
    {
        public override string ToString() => "(?<10>a)()";
    }
    public static Regex WithAlternatePattern() => new AlternatePattern();
    public static Regex Sparse() => new("(?<10>a)(?<named>b)?");
    public static Regex Backwards(string pattern) => new(pattern, RegexOptions.RightToLeft);
}
