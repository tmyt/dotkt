using System;
using System.Runtime.CompilerServices;
using System.Threading;

// Awaitables whose CAPTURE CONTROL (`ConfigureAwait(bool)`) is shaped differently from Task's, so the lowering of
// `await(captureContext = …)` cannot be written against Task and called general (GitHub #64). Each type here is a
// legal .NET awaitable; C# `await` compiles against all three. Consumed by
// tests/interop/consumer/fixtures/CaptureContextAwaitTests.kt.
namespace CaptureAwaitable;

// ---- 1. PERMUTED type arguments -------------------------------------------------------------------------------
// `ConfigureAwait` is declared to return the awaitable's type arguments in the OTHER order. Nothing forbids that,
// and it is what tells a lowering that reconstructs the configured type from the RECEIVER's arguments apart from one
// that reads the declared return type: the former names `ConfiguredPair<Int,String>`, on which none of the members
// it then calls exist.
public sealed class Pair<A, B>
{
    private readonly A _a;
    private readonly B _b;
    private readonly bool _synchronous;

    public Pair(A a, B b, bool synchronous)
    {
        _a = a;
        _b = b;
        _synchronous = synchronous;
    }

    internal A Value => _a;
    internal B Other => _b;
    internal bool Synchronous => _synchronous;

    public PairAwaiter<A, B> GetAwaiter() => new(this);

    // A, B -> B, A.
    public ConfiguredPair<B, A> ConfigureAwait(bool continueOnCapturedContext) => new(this, continueOnCapturedContext);
}

public readonly struct PairAwaiter<A, B> : INotifyCompletion
{
    private readonly Pair<A, B> _pair;
    internal PairAwaiter(Pair<A, B> pair) => _pair = pair;
    public bool IsCompleted => _pair.Synchronous;
    public A GetResult() => _pair.Value;
    public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(_ => continuation());
}

// X is the awaitable's SECOND argument and Y its first — the permutation, carried through.
public readonly struct ConfiguredPair<X, Y>
{
    private readonly Pair<Y, X> _pair;
    private readonly bool _capture;

    internal ConfiguredPair(Pair<Y, X> pair, bool capture)
    {
        _pair = pair;
        _capture = capture;
    }

    public ConfiguredPairAwaiter<X, Y> GetAwaiter() => new(_pair, _capture);
}

public readonly struct ConfiguredPairAwaiter<X, Y> : INotifyCompletion
{
    private readonly Pair<Y, X> _pair;
    private readonly bool _capture;

    internal ConfiguredPairAwaiter(Pair<Y, X> pair, bool capture)
    {
        _pair = pair;
        _capture = capture;
    }

    public bool IsCompleted => _pair.Synchronous;
    public Y GetResult() => _pair.Value;

    // The captured Boolean is observable: with capture requested the continuation runs through the captured
    // SynchronizationContext when there is one, exactly as ConfiguredTaskAwaitable does.
    public void OnCompleted(Action continuation)
    {
        var context = _capture ? SynchronizationContext.Current : null;
        if (context != null) context.Post(_ => continuation(), null);
        else ThreadPool.QueueUserWorkItem(_ => continuation());
    }
}

// ---- 2. a configured awaitable whose GetAwaiter is a referenced EXTENSION --------------------------------------
// The awaitable contract has always accepted `[Extension] static GetAwaiter(this X)` for the awaitable itself; the
// configured awaitable is an awaitable like any other, so it may be entered the same way. This one is ALSO permuted,
// and its extension is generic, so the extension's type arguments have to be unified from its declared receiver
// rather than copied.
public sealed class Duo<A, B>
{
    private readonly A _a;
    private readonly bool _synchronous;

    public Duo(A a, B b, bool synchronous)
    {
        _a = a;
        Other = b;
        _synchronous = synchronous;
    }

    internal A Value => _a;
    internal B Other { get; }
    internal bool Synchronous => _synchronous;

    public DuoAwaiter<A, B> GetAwaiter() => new(this);

    public ConfiguredDuo<B, A> ConfigureAwait(bool continueOnCapturedContext) => new(this);
}

public readonly struct DuoAwaiter<A, B> : INotifyCompletion
{
    private readonly Duo<A, B> _duo;
    internal DuoAwaiter(Duo<A, B> duo) => _duo = duo;
    public bool IsCompleted => _duo.Synchronous;
    public A GetResult() => _duo.Value;
    public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(_ => continuation());
}

// No member GetAwaiter — the only way in is the extension below.
public sealed class ConfiguredDuo<X, Y>
{
    internal ConfiguredDuo(Duo<Y, X> duo) => Duo = duo;
    internal Duo<Y, X> Duo { get; }
}

public static class ConfiguredDuoExtensions
{
    // The receiver's arguments are the method's in order; the RESULT permutes them back to the awaitable's.
    public static DuoAwaiter<Y, X> GetAwaiter<X, Y>(this ConfiguredDuo<X, Y> configured) => new(configured.Duo);
}

// ---- 3. a BYREF-LIKE awaitable --------------------------------------------------------------------------------
// A `ref struct` cannot be the type of an instance field, so it can never be a state-machine slot — but it is a
// perfectly good value inside one invocation, which is all the awaitable itself has to be (the AWAITER is what
// crosses the suspension, and this one is an ordinary struct).
public ref struct RefTick
{
    private readonly int _value;
    public RefTick(int value) => _value = value;
    public RefTickAwaiter GetAwaiter() => new(_value);
    public ConfiguredRefTick ConfigureAwait(bool continueOnCapturedContext) => new(_value);
}

public readonly struct ConfiguredRefTick
{
    private readonly int _value;
    internal ConfiguredRefTick(int value) => _value = value;
    public RefTickAwaiter GetAwaiter() => new(_value);
}

public readonly struct RefTickAwaiter : INotifyCompletion
{
    private readonly int _value;
    internal RefTickAwaiter(int value) => _value = value;
    public bool IsCompleted => true;
    public int GetResult() => _value;
    public void OnCompleted(Action continuation) => continuation();
}

public static class RefTickApi
{
    // A `ref struct` has no Kotlin spelling, so it arrives the way every other byref-like value does: from a .NET
    // signature (cf. ByRefLikeInterop.ByRefLikeApi).
    public static RefTick Make(int value) => new(value);
}
