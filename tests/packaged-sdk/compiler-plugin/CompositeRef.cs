using pluginprobe.api;

namespace FixtureNative;

public static class CompositeRef
{
    public static bool Replace(ref CompositeBox<string, string> first, ref CompositeBox<string, string> second)
    {
        first = new("updated");
        return ReferenceEquals(first, second);
    }
}
