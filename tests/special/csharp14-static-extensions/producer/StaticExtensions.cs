namespace CSharp14StaticExtensions;

public sealed class Alpha;
public sealed class Beta;
public sealed class GenericTarget<T>;
public sealed class ComparableValue : System.IComparable<ComparableValue>
{
    public int CompareTo(ComparableValue? other) => 0;
}

public static class AlphaExtensions
{
    private static int _mutable;

    extension(Alpha)
    {
        public static int Answer() => 42;
        public static string Label => "alpha";
        public static int Mutable
        {
            get => _mutable;
            set => _mutable = value;
        }

        public static int Select(int value) => value;
        public static string Select(string value) => value;
    }
}

// C# emits receiverless implementation methods on the containing type. Keeping the same source member name for a
// second receiver therefore requires a second physical container; the negative fixture pins the opposite case.
public static class BetaExtensions
{
    extension(Beta)
    {
        public static int Answer() => 84;
    }
}

public static class GenericTargetExtensions
{
    extension<T>(GenericTarget<T>) where T : class, System.IComparable<T>
    {
        public static string TypeName() => typeof(T).Name;
    }
}
