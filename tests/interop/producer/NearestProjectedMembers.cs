namespace NearestProjectedMembers;

public class PropertyBase { public int Value => 7; }
public class FieldMiddle : PropertyBase { public new string Value = "field"; }
public class FieldLeaf : FieldMiddle { }

public class FieldBase { public string Value = "base field"; }
public class PropertyMiddle : FieldBase { public new int Value { get; set; } = 9; }
public class PropertyLeaf : PropertyMiddle { }

public class GenericFieldMiddle<T> : PropertyBase
{
    public new T Value;
    public GenericFieldMiddle(T value) { Value = value; }
}
public class GenericFieldLeaf : GenericFieldMiddle<string>
{
    public GenericFieldLeaf() : base("generic") { }
}

public class StaticPropertyBase { public static int Value => 7; }
public class StaticFieldMiddle : StaticPropertyBase { public new static string Value = "static field"; }
public class StaticFieldBase { public static string Value = "base static field"; }
public class StaticPropertyMiddle : StaticFieldBase { public new static int Value { get; set; } = 9; }
public class MixedStaticField : PropertyBase { public new static string Value = "mixed static field"; }
public class MixedInstanceField : StaticPropertyBase { public new string Value = "mixed instance field"; }
public class MixedStaticProperty : FieldBase { public new static int Value { get; set; } = 11; }
public class MixedInstanceProperty : StaticFieldBase { public new int Value { get; set; } = 13; }
public class PrivateStaticGetter
{
    public static int Value { private get; set; }
    public static int Read() => Value;
}

public class ProtectedPropertyBase { protected int Value => 7; }
public class ProtectedFieldMiddle : ProtectedPropertyBase { protected new string Value = "protected"; }
