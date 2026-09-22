namespace ProtectedDefaultAccess;

public class Base
{
    protected static int StaticField = 181;
    protected int InstanceField = 191;
    protected int Property => 193;
    protected int Method() => 197;
}

public class GenericBase<T>
{
    protected T Value;
    public GenericBase(T value) { Value = value; }
}
