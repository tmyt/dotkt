namespace NrtOverrides;

public abstract class Base
{
    public abstract string Describe(string name);
    public abstract string Required(string name);
    public string Describe(int number) => number.ToString();
}

public class Wrapper : Base
{
    public override string Describe(string? name) => name ?? "missing";
    public override string Required(string name) => name;
}

public abstract class GenericBase<T> where T : class
{
    public abstract string Choose(T name, int index);
    public abstract string Choose(int index, T? name);
}
public abstract class Intermediate<U> : GenericBase<U> where U : class { }
public class GenericWrapper : Intermediate<string>
{
    public override string Choose(string? name, int index) => name ?? "first";
    public override string Choose(int index, string? name) => name ?? "second";
}
