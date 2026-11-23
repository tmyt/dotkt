namespace RoundtripPropertyInterop;

public interface IPropertySlot
{
    int value { get; set; }
}

public interface IGenericPropertySlot<T>
{
    T value { get; set; }
}

public sealed class SlotBox<T>(T item) { public T Item { get; } = item; }

public interface IBoxSlot<T> { SlotBox<T> value { get; set; } }

public interface IExplicitIntDefault : IBoxSlot<int>
{
    SlotBox<int> Stored { get; set; }
    SlotBox<int> IBoxSlot<int>.value { get => Stored; set => Stored = value; }
}

public class PropertySlotBaseValue
{
    public PropertySlotBaseValue(string text) => Text = text;
    public string Text { get; }
}

public class PropertySlotDerivedValue : PropertySlotBaseValue
{
    public PropertySlotDerivedValue(string text) : base(text) { }
}

public interface IReadOnlyNominalPropertySlot
{
    PropertySlotBaseValue value { get; }
}

public interface IEmptyDefaultSlot
{
    void touch();
}
