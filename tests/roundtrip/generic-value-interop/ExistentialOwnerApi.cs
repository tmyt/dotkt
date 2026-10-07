namespace GenericValueInterop;

public static class ExistentialOwnerApi
{
    public static ExistentialSlotOwner<string> Echo(ExistentialSlotOwner<string> value) => value;
    public static ExistentialSlotOwner<string> Make() => new("native");
    public static ExistentialSlotOwner<ExistentialSlotOwner<string>> EchoNested(
        ExistentialSlotOwner<ExistentialSlotOwner<string>> value) => value;
    public static ExistentialSlotOwner<ExistentialSlotOwner<string>> MakeNestedOwner() =>
        new(new ExistentialSlotOwner<string>("nested native"));
    public static T Create<T>() where T : new() => new T();
    public static ExistentialSlotOwner<string> Invoke(ExistentialOwnerTransform callback,
        ExistentialSlotOwner<string> value) => callback(value);
    public static ExistentialSlotOwner<string> Dispatch(ExistentialOwnerOverride instance,
        ExistentialSlotOwner<string> value) => instance.Echo(value);
    public static T Read<T>(ExistentialNativeReader<T> reader) => reader.Read();
    public static ExistentialNativeBox<ExistentialSlotOwner<string>> MakeNested() => new(new("nested"));
}

public class ExistentialNativeBase { public int Read() => 23; }
public class ExistentialNativeBox<T>
{
    public T Value;
    public ExistentialNativeBox(T value) { Value = value; }
}

public interface ExistentialNativeReader<T> { T Read(); }

public delegate ExistentialSlotOwner<string> ExistentialOwnerTransform(ExistentialSlotOwner<string> value);

public class ExistentialOwnerStorage
{
    public ExistentialSlotOwner<string> Slot;
    public ExistentialSlotOwner<string> Value { get; set; }
    public ExistentialOwnerStorage(ExistentialSlotOwner<string> value) { Slot = value; Value = value; }
    public ExistentialOwnerStorage(string value) : this(new ExistentialSlotOwner<string>(value)) { }
}

public abstract class ExistentialOwnerOverride
{
    public virtual string Echo(string value) => "base:" + value;
    public abstract ExistentialSlotOwner<string> Echo(ExistentialSlotOwner<string> value);
}
