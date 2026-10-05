namespace GenericValueInterop;

public static class ExistentialOwnerApi
{
    public static ExistentialSlotOwner<string> Echo(ExistentialSlotOwner<string> value) => value;
    public static ExistentialSlotOwner<string> Make() => new("native");
    public static ExistentialSlotOwner<string> Invoke(ExistentialOwnerTransform callback,
        ExistentialSlotOwner<string> value) => callback(value);
    public static ExistentialSlotOwner<string> Dispatch(ExistentialOwnerOverride instance,
        ExistentialSlotOwner<string> value) => instance.Echo(value);
}

public delegate ExistentialSlotOwner<string> ExistentialOwnerTransform(ExistentialSlotOwner<string> value);

public class ExistentialOwnerStorage
{
    public ExistentialSlotOwner<string> Slot;
    public ExistentialSlotOwner<string> Value { get; set; }
    public ExistentialOwnerStorage(ExistentialSlotOwner<string> value) { Slot = value; Value = value; }
}

public abstract class ExistentialOwnerOverride
{
    public virtual string Echo(string value) => "base:" + value;
    public abstract ExistentialSlotOwner<string> Echo(ExistentialSlotOwner<string> value);
}
