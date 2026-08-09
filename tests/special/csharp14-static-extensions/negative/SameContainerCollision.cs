namespace CSharp14StaticExtensions.Negative;

public sealed class Alpha;
public sealed class Beta;

public static class CollidingExtensions
{
    extension(Alpha)
    {
        public static int Answer() => 1;
    }

    extension(Beta)
    {
        public static int Answer() => 2;
    }
}
