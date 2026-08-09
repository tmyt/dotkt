using System;

namespace BidirectionalInterop;

public sealed class Palette
{
    public string Accent => "cyan";
}

public sealed class ReferenceConstrainedTarget<T> where T : class, IComparable<T>
{
}

public sealed class StructConstrainedTarget<T> where T : struct
{
}

public sealed class RefLikeConstrainedTarget<T> where T : allows ref struct
{
}

#pragma warning disable CS0693 // Deliberately probe legal repeated metadata names across a nested generic frame.
public sealed class RepeatedGenericOuter<T>
{
    public sealed class RepeatedGenericInner<T>
    {
    }
}
#pragma warning restore CS0693
