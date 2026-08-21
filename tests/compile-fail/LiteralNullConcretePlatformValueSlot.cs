#nullable enable

using System.Diagnostics.CodeAnalysis;

namespace LiteralNullConcretePlatformValueSlotFixture;

public sealed class PlatformIntBox
{
    [MaybeNull]
    public int Value { get; set; }
}
