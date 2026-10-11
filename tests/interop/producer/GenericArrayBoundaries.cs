namespace GenericArrayBoundaries;

public sealed class Cell<T>
{
    public T[] Value;
    public Cell(T[] value) => Value = value;
}

public interface Transform
{
    T[] Apply<T>(T[] values);
}

public interface JaggedTransform
{
    T[][] Apply<T>(T[][] values);
}

public static class Api
{
    public static T[] Echo<T>(T[] values) => values;
    public static void Replace<T>(ref T[] value, T[] replacement) => value = replacement;
    public static ref T[] Live<T>(Cell<T> cell) => ref cell.Value;
    public static ref T Element<T>(T[] values, int index) => ref values[index];
    public static T[] Invoke<T>(Transform transform, T[] values) => transform.Apply(values);
    public static T[] Nested<T>(System.Collections.Generic.List<T[]> values) => values[0];
    public static T[][] NestedJagged<T>(System.Collections.Generic.List<T[][]> values) => values[0];
    public static T[][] InvokeJagged<T>(JaggedTransform transform, T[][] values) => transform.Apply(values);
    public static void ReplaceJagged<T>(ref T[][] value, T[][] replacement) => value = replacement;
}
