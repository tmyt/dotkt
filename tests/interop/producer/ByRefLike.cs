using System;

namespace ByRefLikeInterop;

// A plain user-defined `ref struct` — byref-like WITHOUT being Span/ReadOnlySpan, so a consumer that recognized
// byref-like-ness by type NAME would miss it. The CLR forbids a value of it as the type of an instance field of a
// non-byref-like type, which is what a coroutine state machine's spill slot is.
public ref struct Tally
{
    public int V;
    public Tally(int v) { V = v; }
}

// Legal same-stem CLR declarations with different generic arity. The generic declaration is byref-like; the
// non-generic declaration is an ordinary heap class and may therefore live in a coroutine state-machine field.
public sealed class StorageCollision
{
    public StorageCollision(int value) => Value = value;
    public int Value { get; }
}

public ref struct StorageCollision<T>
{
    public T Value;
    public StorageCollision(T value) => Value = value;
}

public static class ByRefLikeApi
{
    public static Tally MakeTally(int v) => new Tally(v);
    public static int ReadTally(Tally t) => t.V;

    // A BCL byref-like arriving through an ordinary .NET signature (no `stackBuffer` involved, so it is usable
    // inside a suspend body).
    public static ReadOnlySpan<char> Chars(string s) => s.AsSpan();
    public static int CharsLength(ReadOnlySpan<char> s) => s.Length;

    // `System.Span<T>`, which dll2klib surfaces as the kotc INTRINSIC `kotlin.clr.Span<T>` rather than as an
    // projected metadata record — the one byref-like spelling that cannot come from the metadata flag.
    public static Span<int> MakeSpan(int[] a) => a.AsSpan();
    public static int SpanLength(Span<int> s) => s.Length;
}
