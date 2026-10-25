namespace RoundtripDependentKotlinBound;

public sealed class Owner<Tag, T> where T : roundtrip.dependentnative.ForeignBoundBase<Tag> { }

public static class Api
{
    public static bool Probe<Tag, T>() where T : roundtrip.dependentnative.ForeignBoundBase<Tag> => true;
}
