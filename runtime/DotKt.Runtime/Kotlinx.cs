// CLR forms of kotlinx.coroutines types that aren't part of the language (projection of `kotlinx.coroutines.*` ->
// `DotKtx.Coroutines`, NOT DotKt.Coroutines). These are stopgaps: when the real kotlinx-coroutines-core is compiled
// (Track 2) they ship as the genuine `dotktx.coroutines` assembly and supersede these.
using System;
using System.Threading.Tasks;
using DotKt;             // Result<T>, Unit
using DotKt.Coroutines;  // Continuation<T>, CoroutineContext

namespace DotKtx.Coroutines
{
    /// kotlinx.coroutines.Channel<T> over System.Threading.Channels (T8). suspend send/receive map to the Task
    /// forms; capacity<=0 -> unbounded.
    public sealed class Chan<T>
    {
        readonly System.Threading.Channels.Channel<T> _ch;
        public Chan(int capacity) =>
            _ch = capacity <= 0
                ? System.Threading.Channels.Channel.CreateUnbounded<T>()
                : System.Threading.Channels.Channel.CreateBounded<T>(capacity);
        public Task<int> SendAsync(T v) => _ch.Writer.WriteAsync(v).AsTask().ContinueWith(_ => 0);
        public Task<T> ReceiveAsync() => _ch.Reader.ReadAsync().AsTask();
        public void Close() => _ch.Writer.Complete();
    }

    /// kotlinx.coroutines.CancellableContinuation<T> — a Continuation<T> with cancellation hooks. v1: forwards
    /// resume to the underlying continuation; cancel/invokeOnCancellation are minimal.
    public sealed class CancellableCont<T> : Continuation<T>
    {
        readonly Continuation<T> _inner;
        Action<Exception> _onCancel;
        public CancellableCont(Continuation<T> inner) { _inner = inner; }
        public CoroutineContext Context => _inner.Context;
        public void ResumeWith(Result<T> result) => _inner.ResumeWith(result);
        public bool IsActive => true;
        public void Cancel(Exception cause) { _onCancel?.Invoke(cause); }
        public void InvokeOnCancellation(Action<Exception> handler) { _onCancel = handler; }
    }
}
