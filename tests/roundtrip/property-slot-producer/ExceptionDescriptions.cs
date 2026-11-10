using System;

namespace DotKt.Tests.Suppression;

public class NativeCustomDescription : Exception
{
    public override string ToString() => "native-custom-description";
}

public class NativeHiddenDescription : Exception
{
    public NativeHiddenDescription() : base("native-hidden-message") { }
    public new virtual string ToString() => "hidden-slot-not-object-tostring";
    public string ToString<T>() => "generic-overload-not-object-tostring";
}

public class NativeHiddenDescriptionChild : NativeHiddenDescription
{
    public override string ToString() => "hidden-child-not-object-tostring";
}
