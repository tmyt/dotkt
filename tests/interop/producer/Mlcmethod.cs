// #202 regression: MetadataLoadContext's inherited GetMethods path throws while comparing these generic overrides.
// dll2klib must fall back to declared-only hierarchy enumeration so the ordinary Derived surface is still injectable.
namespace Mlcmethod;

public abstract class GenericBase
{
    public abstract string Describe<T>(T value);
}

public sealed class Derived : GenericBase
{
    public override string Describe<T>(T value) => value?.ToString() ?? "null";
    public int Ping() => 42;
}
