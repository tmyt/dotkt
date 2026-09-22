namespace ExplicitFieldSlots;

public class ExternalFieldBase<A, B>
{
    public B Value;
    public ExternalFieldBase(B value) { Value = value; }
}
public class NullableFieldBase { public string? Value; }
public class ReadonlyFieldBase { public readonly string Value = "readonly base"; }
public class ProtectedFieldBase { protected string Value = "protected base"; }
public class ReferenceFieldBase<T> where T : class { public T Value = null!; }
public class EventFieldBase { public string Changed = "inherited event field"; }
