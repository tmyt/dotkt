namespace RoundtripPropertyInterop;

public interface IPropertySlot
{
    int value { get; set; }
}

public interface IGenericPropertySlot<T>
{
    T value { get; set; }
}

public interface IExplicitIntDefault : IGenericPropertySlot<int>
{
    int Stored { get; set; }
    int IGenericPropertySlot<int>.value { get => Stored; set => Stored = value; }
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
