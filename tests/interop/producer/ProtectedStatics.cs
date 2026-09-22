namespace ProtectedStatics;

public class Base
{
    protected static int Field = 61;
    protected static int Property { get; set; } = 67;
    protected static int Method(int value) => value + 2;
    public static int Restricted { get; protected set; } = 71;
    public static int ReadField() => Field;
    public static int ReadProperty() => Property;
    public static int Visible = 101;
    public static int VisibleProperty => 107;
    public static int VisibleMethod(int value) => value + 109;
}
public class Middle : Base
{
    protected new static int Visible = 103;
    protected new static int VisibleProperty => 113;
    protected new static int VisibleMethod(int value) => value + 127;
}
public class Leaf : Middle { }
public class GenericBase<T>
{
    protected static T Value;
    protected static T Store(T value) { Value = value; return value; }
    public static T Read() => Value;
}
public class StringLeaf : GenericBase<string> { }
public static class Reader
{
    public static int Field() => Leaf.Visible;
    public static int Property() => Leaf.VisibleProperty;
    public static int Method(int value) => Leaf.VisibleMethod(value);
}
