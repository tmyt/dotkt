using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MyLib
{
    // A custom awaitable mirroring the WinRT IAsyncOperation<T> shape: awaitable via a GENERIC EXTENSION GetAwaiter
    // (NOT a member) that returns a struct awaiter conforming to the .NET await pattern. This is the vehicle that
    // proves bir2cir's extension-GetAwaiter path (clrGenericStatic emission + generic-receiver unification) WITHOUT
    // needing the WinRT projection assembly.
    public sealed class MyOp<T>
    {
        internal readonly T Value;
        internal readonly bool Sync;   // true -> already completed (IsCompleted true); false -> suspends then resumes
        public MyOp(T value, bool sync) { Value = value; Sync = sync; }
    }

    public readonly struct MyAwaiter<T> : INotifyCompletion
    {
        private readonly MyOp<T> _op;
        public MyAwaiter(MyOp<T> op) { _op = op; }
        public bool IsCompleted => _op.Sync;
        public T GetResult() => _op.Value;
        // Suspend path: schedule the resume on the threadpool (like TaskAwaiter over Task.Delay) — the blockOn harness
        // drains it through its Monitor Wait/Pulse. Deterministic RESULT (the value is known); timing is irrelevant.
        public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(_ => continuation());
    }

    public static class MyOpExtensions
    {
        // The generic extension GetAwaiter — the WindowsRuntimeSystemExtensions.GetAwaiter<TResult> analog.
        public static MyAwaiter<T> GetAwaiter<T>(this MyOp<T> op) => new MyAwaiter<T>(op);
    }
}
