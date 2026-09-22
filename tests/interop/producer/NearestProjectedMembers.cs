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

public class ProtectedPropertyBase { protected int Value => 7; }
public class ProtectedFieldMiddle : ProtectedPropertyBase { protected new string Value = "protected"; }
