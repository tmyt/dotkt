namespace NrtOverrides;

public abstract class Base
{
    public abstract string Describe(string name);
    public abstract string Required(string name);
    public abstract object Make(string name);
    public abstract string this[string name] { get; }
    public string Describe(int number) => number.ToString();
}

public class Wrapper : Base
{
    public override string Describe(string? name) => name ?? "missing";
    public override string Required(string name) => name;
    public override string Make(string? name) => name ?? "made";
    public override string this[string? name] => name ?? "indexed";
}

public class Invariant<T> { }

public abstract class GenericBase<T> where T : class
{
    public abstract string Choose(T name, int index);
    public abstract string Choose(int index, T? name);
    public abstract int Count(Invariant<T?> value);
}
public abstract class Intermediate<U> : GenericBase<U> where U : class { }
public class GenericWrapper : Intermediate<string>
{
    public override string Choose(string? name, int index) => name ?? "first";
    public override string Choose(int index, string? name) => name ?? "second";
    // The CLR slot is invariant even when its reference annotations disagree.
#pragma warning disable CS8610
    public override int Count(Invariant<string> value) => 1;
#pragma warning restore CS8610
}
