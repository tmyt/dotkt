namespace ConstructorSelectionInterop;

public sealed class SelectedConstructor<T>
{
    public int Chosen { get; }
    public SelectedConstructor(T value) { Chosen = 1; }
    public SelectedConstructor(int marker) { Chosen = 2; }
}
