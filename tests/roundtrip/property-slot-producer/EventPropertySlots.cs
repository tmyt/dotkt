namespace EventPropertySlotInterop;

public interface IHasObject
{
    object Changed { get; }
}

public interface IHandleProperty
{
    object PropertyChanged { get; }
}

public interface IMixedEvent : System.ComponentModel.INotifyPropertyChanged, IHandleProperty { }
