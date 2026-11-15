namespace ConstructorKindInterop;

public sealed class NativeConstructorKinds
{
    public int Value { get; }
    public NativeConstructorKinds() : this(7) { }
    public NativeConstructorKinds(int value) { Value = value; }
}

public sealed class NativeConstructorKindAttribute : System.Attribute
{
    public int Value { get; }
    public NativeConstructorKindAttribute(int value) { Value = value; }
}
