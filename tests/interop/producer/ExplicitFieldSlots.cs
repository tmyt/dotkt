#nullable enable
namespace ExplicitFieldSlots;

public interface IValue<T> { T Value { get; } }
public interface IMutableValue<T> { T Value { get; set; } }
public interface IReferenceValue<T> where T : class { T Value { get; set; } }
public class ReferenceField<T> : ReferenceFieldBase<T>, IReferenceValue<T> where T : class
{
    T IReferenceValue<T>.Value { get; set; } = null!;
}

internal interface IHiddenDefaultValue : IValue<int> { int IValue<int>.Value => 61; }
public class DefaultDirectField : IHiddenDefaultValue { public string Value = "default direct"; }
public class DefaultInheritedField : FieldBase, IHiddenDefaultValue { }
public interface IStaticDefaultValue : IValue<int>
{
    public new static string Value = "interface static";
    int IValue<int>.Value => 67;
}

public interface IChanged { event System.Action<int> Changed; }
public static class EventSlotCounters { public static int Added; public static int Removed; }
internal interface IHiddenDefaultEvent : IChanged
{
    event System.Action<int> IChanged.Changed
    {
        add { EventSlotCounters.Added++; value(71); }
        remove { EventSlotCounters.Removed++; }
    }
}
public class DefaultEventField : IHiddenDefaultEvent { public string Changed = "event field"; }
public class DefaultInheritedEventField : EventFieldBase, IHiddenDefaultEvent { }

public class DirectField : IMutableValue<int>
{
    public string Value = "direct";
    private int _slot = 11;
    int IMutableValue<int>.Value { get => _slot; set => _slot = value; }
}
public class FieldBase { public string Value = "base"; }
public class InheritedField : FieldBase, IMutableValue<int>
{
    private int _slot = 13;
    int IMutableValue<int>.Value { get => _slot; set => _slot = value; }
}
public class SameTypeField : IMutableValue<int>
{
    public int Value = 17;
    private int _slot = 19;
    int IMutableValue<int>.Value { get => _slot; set => _slot = value; }
}
public class GenericField<A, B> : ExternalFieldBase<B, A>, IValue<B>
{
    private readonly B _slot;
    public GenericField(A value, B slot) : base(value) { _slot = slot; }
    B IValue<B>.Value => _slot;
}
public class NullableField : NullableFieldBase, IValue<int> { int IValue<int>.Value => 23; }
public class ReadonlyField : ReadonlyFieldBase, IValue<int> { int IValue<int>.Value => 29; }
public class ProtectedField : ProtectedFieldBase, IValue<int> { int IValue<int>.Value => 31; }
public class DirectReadonlyField : IValue<int>
{
    public readonly string Value = "readonly direct";
    int IValue<int>.Value => 37;
}
public class StaticField : IValue<int>
{
    public static string Value = "static";
    int IValue<int>.Value => 41;
}
