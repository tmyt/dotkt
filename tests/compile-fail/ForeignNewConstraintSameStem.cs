namespace DeclarationIdentityInterop;

public sealed class ConstructorCollision
{
    private ConstructorCollision() { }
}

public readonly struct ConstructorCollision<T>
{
}

public sealed class ConstructorSegmentOuter<T>
{
    public readonly struct Leaf<U>
    {
    }
}

public sealed class ConstructorSegmentOuter
{
    public sealed class Leaf<T, U>
    {
        private Leaf() { }
    }
}

public sealed class StorageCollision
{
}

public ref struct StorageCollision<T>
{
    public T Value;
}

public sealed class StorageSegmentOuter<T>
{
    public ref struct Leaf<U>
    {
        public U Value;
    }
}

public sealed class StorageSegmentOuter
{
    public sealed class Leaf<T, U>
    {
    }
}

public static class Constraints
{
    public static int NeedsNew<T>() where T : new() => 1;
}
