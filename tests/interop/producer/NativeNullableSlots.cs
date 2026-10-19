namespace NativeNullableSlots;

public interface ISource { int? Item { get; } }
public interface IMutableSource { long? Item { get; set; } }
public abstract class BaseSource { public abstract int? Item { get; } }
public interface ITransform { int? Transform(int? value); }

public static class Api
{
    public static int? Read(ISource value) => value.Item;
    public static long? ReadMutable(IMutableSource value) => value.Item;
    public static void Write(IMutableSource value, long? item) => value.Item = item;
    public static int? ReadBase(BaseSource value) => value.Item;
    public static int? Apply(ITransform value, int? item) => value.Transform(item);
}
