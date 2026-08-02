// A plain C# NRT-enabled producer for #367. These families have identical CLR prefix types; only nullable-reference
// metadata makes the fixed overload's Kotlin view wider than the params overload's prefix. C# ignores that metadata
// during overload resolution and chooses the fixed form. dll2klib supplies a metadata-only narrowed view of that same
// physical member so stock Kotlin resolution reaches its non-vararg tiebreak and makes the same choice.
namespace NrtParams
{
    public static class Api
    {
        public static string Pick(string? value) => "fixed:" + (value ?? "<null>");
        public static string Pick(string format, params object?[] args) => "params:" + args.Length;

        // Control: these physical prefix types are genuinely different. Both C# and Kotlin must choose the String
        // params overload for a String argument; #367 must not manufacture an Object-to-String bridge.
        public static string Different(object? value) => "object";
        public static string Different(string format, params object?[] args) => "params:" + args.Length;

        public static string Generic<T>(T? value) where T : class => "fixed:" + (value is null ? "<null>" : "value");
        public static string Generic<T>(T format, params object?[] args) where T : class => "params:" + args.Length;

        public static string Pair(string? value, int count) => "fixed:" + (value ?? "<null>") + ":" + count;
        public static string Pair(string value, int count, params object?[] args) => "params:" + args.Length;
    }

    public sealed class FinalApi
    {
        public string Pick(string? value) => "fixed:" + (value ?? "<null>");
        public string Pick(string format, params object?[] args) => "params:" + args.Length;
    }

    public class VirtualApi
    {
        public virtual string Pick(string? value) => "fixed:" + (value ?? "<null>");
        public virtual string Pick(string format, params object?[] args) => "params:" + args.Length;
    }

    public sealed class CtorProbe
    {
        public string Which { get; }

        public CtorProbe(string? value) => Which = "fixed:" + (value ?? "<null>");
        public CtorProbe(string format, params object?[] args) => Which = "params:" + args.Length;
    }

    public static class Extensions
    {
        public static string Pick(this string? value) => "fixed:" + (value ?? "<null>");
        public static string Pick(this string format, params object?[] args) => "params:" + args.Length;
    }
}
