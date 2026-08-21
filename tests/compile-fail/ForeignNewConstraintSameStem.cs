namespace DeclarationIdentityInterop;

public sealed class ConstructorCollision
{
    private ConstructorCollision() { }
}

public readonly struct ConstructorCollision<T>
{
}

public sealed class StorageCollision
{
}

public ref struct StorageCollision<T>
{
    public T Value;
}

public static class Constraints
{
    public static int NeedsNew<T>() where T : new() => 1;
}
