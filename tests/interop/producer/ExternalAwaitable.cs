using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ExternalAwaitable;

public sealed class Operation<T>
{
    internal readonly TaskCompletionSource<T> Source = new();
    public Operation(T value, bool synchronous)
    {
        if (synchronous) Source.SetResult(value);
    }
    public void Complete(T value) => Source.SetResult(value);
}

public static class OperationExtensions
{
    public static TaskAwaiter<T> GetAwaiter<T>(this Operation<T> operation) => operation.Source.Task.GetAwaiter();
}

public sealed class MemberOperation<T>
{
    private readonly TaskCompletionSource<T> _source = new();
    public MemberOperation(T value, bool synchronous)
    {
        if (synchronous) _source.SetResult(value);
    }
    public void Complete(T value) => _source.SetResult(value);
    public TaskAwaiter<T> GetAwaiter() => _source.Task.GetAwaiter();
}
