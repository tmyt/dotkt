namespace RoundtripPropertyInterop;

public interface IPropertySlot
{
    int value { get; set; }
}

public interface IGenericPropertySlot<T>
{
    T value { get; set; }
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
