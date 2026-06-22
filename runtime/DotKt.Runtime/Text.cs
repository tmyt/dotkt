// CLR forms of kotlin.text helpers that need a tiny runtime shim (projection of kotlin.text -> DotKt.Text).
// Regex.matches (FULL match — .NET IsMatch is partial) and Regex.find (Kotlin returns MatchResult? = null when no
// match, but .NET Match is always non-null with Success=false) need a wrapper; the compiler maps them here.
using System.Text.RegularExpressions;

namespace DotKt.Text
{
    public static class Regexes
    {
        /// kotlin.text.Regex.matches(input): the ENTIRE input matches.
        public static bool Matches(Regex r, string s) { var m = r.Match(s); return m.Success && m.Index == 0 && m.Length == s.Length; }
        /// kotlin.text.Regex.find(input): first match, or null (Kotlin MatchResult? — .NET Match maps directly, .value -> .Value).
        public static Match Find(Regex r, string s) { var m = r.Match(s); return m.Success ? m : null; }
    }
}
