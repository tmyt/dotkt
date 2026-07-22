using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ExtensionAwaitable;

public sealed class Operation<T>
{
    internal readonly T Value;
    internal readonly bool Synchronous;
    public Operation(T value, bool synchronous) { Value = value; Synchronous = synchronous; }
}

public readonly struct OperationAwaiter<T> : INotifyCompletion
{
    private readonly Operation<T> _operation;
    public OperationAwaiter(Operation<T> operation) => _operation = operation;
    public bool IsCompleted => _operation.Synchronous;
    public T GetResult() => _operation.Value;
    public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(_ => continuation());
}

public static class OperationExtensions
{
    public static OperationAwaiter<T> GetAwaiter<T>(this Operation<T> operation) => new(operation);
}
