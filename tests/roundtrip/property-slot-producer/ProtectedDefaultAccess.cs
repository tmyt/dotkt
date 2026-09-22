namespace ProtectedDefaultAccess;

public class Base
{
    protected static int StaticField = 181;
    protected int InstanceField = 191;
    protected readonly int ReadonlyField = 241;
    protected volatile int VolatileField = 251;
    protected int Property => 193;
    protected int Method() => 197;
}

public class GenericBase<T>
{
    protected T Value;
    protected static T StaticValue;
    protected T Echo(T value) => value;
    public GenericBase(T value) { Value = value; StaticValue = value; }
}
