#nullable enable

namespace SlotTableInterop;

public interface IOverloaded<T>
{
    int Measure(string? value);
    int Measure(T? value);
}
