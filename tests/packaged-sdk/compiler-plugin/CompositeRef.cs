using pluginprobe.api;

namespace FixtureNative;

public static class CompositeRef
{
    // An indexed getter and a non-indexed setter cannot share a CLR Property signature.
    public static bool HasWritablePropertyRow() => typeof(Writable).GetProperty("number") != null;

    public static bool Replace(ref CompositeBox<string, string> first, ref CompositeBox<string, string> second)
    {
        first = new("updated");
        return ReferenceEquals(first, second);
    }
}
