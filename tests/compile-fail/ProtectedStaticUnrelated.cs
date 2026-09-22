namespace ProtectedStaticNegative;
public class Base
{
    protected static int Field;
    protected static int Method() => 1;
    public static int Property { get; protected set; }
}
