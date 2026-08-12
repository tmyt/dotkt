namespace CompileFailForeignPrivateDefault;

public interface IPropertySlot
{
    int Value { get; }
}

// C# emits the explicit interface accessor as a private final DIM body. A consumer can inherit it through this
// interface, but another assembly cannot call that body to manufacture a new class-level MethodImpl trampoline.
public interface IPrivateDefaultProperty : IPropertySlot
{
    int IPropertySlot.Value => 42;
}
