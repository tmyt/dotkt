using roundtrip.genericvalueinterop;
using roundtrip.invariantstorage;

namespace GenericValueInterop;

public interface ILeftMark { }
public interface IRightMark { }
public interface IProjectedMark : ILeftMark, IRightMark { }
public interface IRichProjectedMark : IProjectedMark { }
public class SharedNativeStream<T> : System.IO.MemoryStream, IProjectedMark { }
public sealed class NativeResultProbe
{
    private System.Array backing;
    public ref T Location<T>(T value) where T : System.IO.MemoryStream, IProjectedMark
    {
        T[] values = [value];
        backing = values;
        return ref values[0];
    }
    public System.Span<T> Slice<T>(T value) where T : System.IO.MemoryStream, IProjectedMark
    {
        T[] values = [value, default];
        backing = values;
        return values;
    }
    public object Read(int index) => backing.GetValue(index);
    public static int Size<T>(System.Span<T> values) => values.Length;
    public static void FillSecond<T>(System.Span<T> values) => values[1] = values[0];
}
public class NativeStreamBase<T> where T : System.IO.MemoryStream, IProjectedMark
{
    public T FromNative(T item) => item;
}
public interface IMarkedTag<T> { }
public interface IMarkedRecord<T> where T : IProjectedMark { T Read(); void Write(T item); }
public interface IMarkedRecordTag<T> where T : IProjectedMark { }
public sealed class TwoMarkedTags : IMarkedTag<int>, IMarkedTag<string> { }
public sealed class ForeignFirstSelector : roundtrip.constrainedbound.MarkedSelector
{
    public override object selectMarkedProjected<A, Storage, Proof>(object left, object right) => left;
    public static object FromExact(roundtrip.constrainedbound.MarkedSelector selector,
        roundtrip.constrainedbound.ConcreteMarkedItem first, roundtrip.constrainedbound.ConcreteMarkedItem last) =>
        selector.selectMarked<roundtrip.constrainedbound.ConcreteMarkedItem, roundtrip.constrainedbound.ConcreteMarkedItem>(first, last);
}
public class MarkedStreamReceiver
{
    public virtual string Source<T>(T item) where T : System.IO.MemoryStream, IProjectedMark => "base";
    public virtual int SourceRef<T>(T item, ref int n) where T : System.IO.MemoryStream, IProjectedMark => n = 42;
    protected int Inherited<T>(T item, ref int n) where T : System.IO.MemoryStream, IProjectedMark => ++n;
    protected MarkedStreamReceiver Receiver<T>(T item, ref int n) where T : System.IO.MemoryStream, IProjectedMark
    { ++n; return this; }
    public T Keep<T>(T item) where T : System.IO.MemoryStream, IProjectedMark => item;
    public T WriteAliased<T>(T item, ref int first, ref int second) where T : System.IO.MemoryStream, IProjectedMark
    {
        first = 8;
        if (second != 8) throw new System.InvalidOperationException("Expected the same managed location");
        second = 42;
        return item;
    }
}

public interface NativeBoxReceiver
{
    object Accept(Box<string, string> value);
}

public sealed class NativeBoxContainer<T>(T value) { public T Value { get; } = value; }
public sealed class NativeBoxArrayLease(Box<string, string>[] values)
{
    public object ArrayIdentity => values;
    public Box<string, string> First { get => values[0]; set => values[0] = value; }
}
public class NativeVectorBase(Derived<string>[] values) { public int Count => values.Length; }
public delegate NativeBoxContainer<Box<string, string>> NativeBoxContainerFactory();
public interface NativeBoxCreator { Box<string, string> Create(); }
public interface NativeBoxArraySource { Box<string, string>[] Read(); }
public delegate Box<string, string>[] NativeRowFactory();
public interface NativeValueSource<T> { T Read(); }
public struct NativeRowComparer(int score) : System.IComparable<Box<string, string>[]>
{
    public int CompareTo(Box<string, string>[] values) => values[0] is null ? 0 : score;
}
public delegate Box<string, string> NativeBoxFactory();
public delegate object NativeBoxConsumer(Box<string, string> value);

public static class NativeBoxApi
{
    public static T ReplaceRecord<T>(IMarkedRecord<T> record, T item) where T : IProjectedMark
    {
        record.Write(item);
        return record.Read();
    }
    public static bool HasRecordTag<T>(object record) where T : IProjectedMark => record is IMarkedRecordTag<T>;
    public static string InterfaceWitness<T>() where T : IProjectedMark => typeof(T).Name;
    public static string JointInterfaceWitness<T>() where T : ILeftMark, IRightMark => typeof(T).Name;
    public static T LastMarked<T>(T first, T second) where T : IProjectedMark => second;
    public static B SecondMarkedStream<A, B>(A first, B second)
        where A : System.IO.MemoryStream, IProjectedMark where B : System.IO.MemoryStream, IProjectedMark => second;
    public static string FirstMarkedType<A, B>(A first, B second, IMarkedTag<int> tag)
        where A : System.IO.MemoryStream, IProjectedMark where B : System.IO.MemoryStream, IProjectedMark => typeof(A).Name;
    public static B DependentMarkedStream<A, B>(A first, B second)
        where A : System.IO.MemoryStream, IProjectedMark where B : A => second;
    static Box<string, string> live = new("initial");
    public static void ResetLive() => live = new("initial");
    public static ref Box<string, string> Live() => ref live;
    public static Box<string, string> LiveValue() => live;
    public static void SetLive(Box<string, string> value) => live = value;

    public static Box<string, string> Echo(Box<string, string> value) => value;
    public static Box<string, string>[] EchoBoxes(Box<string, string>[] values) => values;
    public static NativeBoxArrayLease RetainBoxes(Box<string, string>[] values) => new(values);
    public static bool FirstMissing(ref Box<string, string>[] values) => values[0] is null;
    public static void ResetBoxes(out Box<string, string>[] values) => values = [new("reset")];
    public static int CountBoth(Box<string, string>[] first, Box<string, string>[] second) => first.Length + second.Length;
    public static bool ContainsSlot(ref Box<string, string> slot, Box<string, string>[] values) =>
        System.Array.IndexOf(values, slot) >= 0;
    public static Box<string, string>[] ArrayField;
    public static ref Box<string, string>[] ArrayLocation() => ref ArrayField;
    public static Box<string, string>[] ArrayProperty { get; set; }
    public static object ReadFirst(NativeBoxArraySource source) => source.Read()[0];
    public static object ReadFactoryRow(NativeRowFactory source) => source()[0];
    public static object ReadFirstRow(Box<string, string>[][] matrix) => matrix[0][0];
    public static object ReadArrayRows(System.Array[] rows) => rows[0].GetValue(0);
    public static object ReadGenericSource(NativeValueSource<System.Array> source) => source.Read().GetValue(0);
    public static bool IsNativeBoxMatrix(object value) => value is Box<string, string>[][];
    public static NativeBoxContainer<Box<string, string>> EchoContainer(NativeBoxContainer<Box<string, string>> value) => value;
    public static NativeBoxContainer<Box<string, string>> InvokeContainer(NativeBoxContainerFactory factory) => factory();
    public static Box<string, string> InvokeCreator(NativeBoxCreator factory) => factory.Create();
    public static Box<string, string> InvokeFactory(NativeBoxFactory factory) => factory();
    public static object InvokeConsumer(NativeBoxConsumer consumer, Box<string, string> value) => consumer(value);
    public static StoredVariant<int, int> CreateVariant() => new(42);
    public static int ReadVariant(StoredVariant<int, int> value) => (int)value.value;
    public static object Dispatch(NativeBoxReceiver receiver, Box<string, string> value) => receiver.Accept(value);

    public static bool ReplaceAliased(ref Box<string, string> first, ref Box<string, string> second)
    {
        first = new Box<string, string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string, string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}
