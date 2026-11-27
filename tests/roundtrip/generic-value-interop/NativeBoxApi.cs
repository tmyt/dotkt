using roundtrip.genericvalueinterop;
using roundtrip.invariantstorage;
using WideIntCallback = DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int, int, int, int,
    int, int, int, int, int, int, int, int, int>;
using WideUnitCallback = DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int, int, int, int,
    int, int, int, int, int, int, int, int, kotlin.Unit>;

namespace GenericValueInterop;

using WideIntFactory = DotKt.Runtime.CompilerServices.KFunc<object, object, object, object, object, object,
    object, object, object, object, object, object, object, object, object, object, object, WideIntCallback>;

public interface ILeftMark { }
public delegate T MarkedValue<T>() where T : ILeftMark;
public interface IRightMark { }
public interface IProjectedMark : ILeftMark, IRightMark { }
public interface IRichProjectedMark : IProjectedMark { }
public interface IJointTag<T> where T : ILeftMark, IRightMark { }
public class SharedNativeStream<T> : System.IO.MemoryStream, IProjectedMark { }
public class NativeOpenVisitor<T>
{
    public virtual T Visit(T item) => throw new System.NotImplementedException();
    public T Dispatch(T item) => Visit(item);
}
public class NativeMiddleVisitor<TUnused,T> : NativeOpenVisitor<T>
{
    public virtual object Visit(object item) => "middle";
    public object DispatchOther(object item) => Visit(item);
}
public class NativeBoundVisitor<T> where T : System.IO.Stream
{
    public virtual object Visit(T item) => "base";
    public virtual object Visit(System.IO.Stream item) => "other";
    public object Dispatch(T item) => Visit(item);
    public object DispatchStream(System.IO.Stream item) => Visit(item);
}
public sealed class NativeStreamSlot<T> where T : System.IO.Stream
{
    public T Value { get; set; }
    public bool UsesExactType(T item) => typeof(T) == item.GetType();
}
public static class NestedBounds
{
    public class Root { }
    public class Mid : Root { }
    public interface ISink<in T> { }
    public sealed class MidSink : ISink<Mid> { }
    public sealed class Slot<T> where T : Root { public T Value { get; set; } }
    public sealed class Joint<T, S> where T : Root where S : ISink<T> { public T Value { get; set; } }
}
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
    public static void Replace<T>(ref T location, T value) where T : System.IO.MemoryStream, IProjectedMark =>
        location = value;
    public static int Size<T>(System.Span<T> values) => values.Length;
    public void WriteSecond<T>(int before, System.Span<T> values, int after, T replacement)
        where T : System.IO.MemoryStream, IProjectedMark
    {
        if (before != 1 || after != 3) throw new System.InvalidOperationException("Unexpected operand order");
        values[1] = replacement;
    }
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
public class NativeBoxArrayLease(Box<string, string>[] values)
{
    public object ArrayIdentity => values;
    public Box<string, string> First { get => values[0]; set => values[0] = value; }
}
public class NativeVectorBase(Derived<string>[] values) { public int Count => values.Length; }
public class NativeCallbackBase(DotKt.Runtime.CompilerServices.KFunc<object, object, object, object, object, object,
    object, object, object, object, object, object, object, object, object, object, object, object> callback)
{
    public object Read() => callback(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
}
public class NativeIntCallbackBase(DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int,
    int, int, int, int, int, int, int, int, int, int, int, int> callback)
{
    public int CallbackReads { get; private set; }
    public WideIntCallback Callback { get { CallbackReads++; return callback; } set => callback = value; }
    public WideIntCallback ReadCallback(object tag) => Callback;
    public int Read() => Callback(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
}
public class NestedCallbackBase(DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int, int, int, int,
    int, int, int, int, int, int, int, int, WideIntCallback> callback)
{
    public int Read() => NativeWideCallbacks.Read(callback(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17));
}
public sealed class NativeCallbackOwner<T>(DotKt.Runtime.CompilerServices.KFunc<T, int, int, int, int, int,
    int, int, int, int, int, int, int, int, int, int, int, T> callback)
{
    public T Read(T first) => callback(first, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
}
public delegate WideIntCallback WideFactory();
public class NativeCallbackScope
{
    protected static int Visit<T>(WideUnitCallback callback, int tag = 71) =>
        NativeWideCallbacks.VisitUnit(callback) ? tag : -1;
    public virtual int Choose(WideUnitCallback callback, int tag = 19) => -1;
    public static bool operator <(NativeCallbackScope a, WideIntCallback b) => false;
    public static bool operator >(NativeCallbackScope a, WideIntCallback b) => false;
}
public class NativeCallbackOverride : NativeCallbackScope
{
    public override int Choose(WideUnitCallback callback, int tag) =>
        NativeWideCallbacks.VisitUnit(callback) ? tag : -1;
}
public interface IVisitWide { bool Apply(WideUnitCallback callback); }
public struct WideVisitor : IVisitWide
{
    public bool Apply(WideUnitCallback callback) => NativeWideCallbacks.VisitUnit(callback);
}
public abstract class NativeVisitor
{
    public abstract bool Accept<T>(T value) where T : struct, IVisitWide;
    public bool Run() => Accept(new WideVisitor());
}
public static class NativeWideCallbacks
{
    public static WideIntFactory Factory => (a, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, q) =>
        (first, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, last) => (int)a - (int)q + first - last;
    public static DotKt.Runtime.CompilerServices.KFunc<object, object, object, object, object, object, object,
        object, object, object, object, object, object, object, object, object, object,
        DotKt.Runtime.CompilerServices.KFunc<MarkedValue<T>, int, int, int, int, int, int, int, int,
            int, int, int, int, int, int, int, int, int>> SelectFactory<T>(T tag) where T : ILeftMark =>
        (_, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) =>
            (pick, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _) => ReferenceEquals(pick(), tag) ? 42 : -1;
    public static bool VisitExtension(this NativeCallbackScope scope, WideUnitCallback callback) => VisitUnit(callback);
    public static bool VisitUnit(WideUnitCallback callback) =>
        callback(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17) is not null;
    public static void VisitVoid(DotKt.Runtime.CompilerServices.KAction<int, int, int, int, int, int, int, int, int,
        int, int, int, int, int, int, int, int> callback) =>
        callback(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
    public static int ReadFactory(WideFactory factory) => Read(factory());
    public static int ReadNested(DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int, int, int, int,
        int, int, int, int, int, int, int, int, WideIntCallback> factory) => new NestedCallbackBase(factory).Read();
    public static int Read<T>(DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int, int, int, int,
        int, int, int, int, int, int, int, int, int> callback) => Read(callback);
    public static int Read<T>(DotKt.Runtime.CompilerServices.KFunc<object, object, object, object, object, object,
        object, object, object, object, object, object, object, object, object, object, object, object> callback) => -1;
    public static int Read(DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int, int, int, int,
        int, int, int, int, int, int, int, int, int> callback) =>
        callback(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
    public static int Read(DotKt.Runtime.CompilerServices.KFunc<string, string, string, string, string, string,
        string, string, string, string, string, string, string, string, string, string, string, int> callback) => -1;
    public static DotKt.Runtime.CompilerServices.KFunc<object, object, object, object, object, object, object,
        object, object, object, object, object, object, object, object, object, object, int> IntResult =>
        (a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q) => (int)a + (int)q;
}
public class NativeArrayCallbackBase(DotKt.Runtime.CompilerServices.KFunc<int, int, int, int, int, int,
    int, int, int, int, int, int, int, int, int, int, int, object> callback)
{
    public object Read() => callback(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
}
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
    public static string IndependentType<A, B, Z>(A first, B second, Z independent)
        where A : System.IO.MemoryStream, IProjectedMark where B : A => typeof(Z).Name;
    public static string RightWitness<P,T>(P item) where P : System.IO.MemoryStream, ILeftMark where T : IRightMark => typeof(T).Name;
    public static void ReplaceLocation<P,T>(P item, ref T target, T next) where P : System.IO.MemoryStream, IProjectedMark => target = next;
    static Box<string, string> live = new("initial");
    public static void ResetLive() => live = new("initial");
    public static ref Box<string, string> Live() => ref live;
    public static Box<string, string> LiveValue() => live;
    public static void SetLive(Box<string, string> value) => live = value;

    public static Box<string, string> Echo(Box<string, string> value) => value;
    public static Box<string, string>[] EchoBoxes(Box<string, string>[] values) => values;
    public static NativeBoxArrayLease RetainBoxes(Box<string, string>[] values) => new(values);
    public static System.Func<object> RetainBagReader(object bag)
    {
        var retained = (System.Collections.IList)bag;
        return () => ((System.Array)retained[0]).GetValue(0);
    }
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

    public static bool ReplaceCaptured<T>(CaptureBox<T> witness, ref Box<string, string> slot, Box<string, string>[] other)
        where T : System.IDisposable, allows ref struct
    {
        slot = new Box<string, string>("changed");
        return typeof(T) == typeof(System.IO.MemoryStream);
    }

    public static bool ReplaceAliased(ref Box<string, string> first, ref Box<string, string> second)
    {
        first = new Box<string, string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string, string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}
