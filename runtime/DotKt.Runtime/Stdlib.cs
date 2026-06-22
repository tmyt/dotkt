// CLR forms of the kotlin.* ROOT stdlib types (projection of the `kotlin.*` root namespace -> `DotKt`). These are
// language/stdlib core, NOT kotlin.coroutines — kept out of the DotKt.Coroutines namespace deliberately.
using System;

namespace DotKt
{
    /// kotlin.Result<T> — a success value or a failure exception. Carried by Continuation.ResumeWith; produced by
    /// runCatching. A plain struct (no JVM-style boxing of the success value into a sentinel wrapper).
    public readonly struct Result<T>
    {
        readonly T _value;
        readonly Exception _ex;
        Result(T v, Exception e) { _value = v; _ex = e; }
        public static Result<T> Success(T v) => new Result<T>(v, null);
        public static Result<T> Failure(Exception e) => new Result<T>(default, e);
        public bool IsFailure => _ex != null;
        public bool IsSuccess => _ex == null;            // kotlin.Result.isSuccess
        public T Value => _value;                        // the success value (read on the success branch)
        public Exception ExceptionOrNull => _ex;         // kotlin.Result.exceptionOrNull()
        public T GetOrThrow() { if (_ex != null) throw _ex; return _value; }
    }

    /// kotlin.Unit as a real type — needed when Unit is a generic TYPE ARGUMENT (Continuation<Unit>, Result<Unit>,
    /// Deferred<Unit>): a CLR generic arg can't be System.Void, so it erases to this singleton. (In return/statement
    /// position Unit still lowers to `void`.) See T7 / docs §13r.
    public sealed class Unit
    {
        public static readonly Unit Instance = new Unit();
        Unit() { }
        public override string ToString() => "kotlin.Unit";
    }
}
