using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ExternalAwaiterContracts;

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
    public static OptionalAwaiter<T> GetAwaiter<T>(this OptionalOperation<T> operation) where T : class => new(operation.Source.Task);
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

public sealed class OptionalOperation<T> where T : class
{
    internal readonly TaskCompletionSource<T> Source = new();
    public OptionalOperation(bool synchronous)
    {
        if (synchronous) Source.SetResult(default);
    }
    public void Complete() => Source.SetResult(default);
}

public sealed class OptionalMemberOperation<T> where T : class
{
    private readonly TaskCompletionSource<T> _source = new();
    public OptionalMemberOperation(bool synchronous)
    {
        if (synchronous) _source.SetResult(default);
    }
    public void Complete() => _source.SetResult(default);
    public OptionalAwaiter<T> GetAwaiter() => new(_source.Task);
}

public sealed class TextOperation
{
    private readonly TaskCompletionSource<string> _source = new();
    public TextOperation(string value, bool synchronous)
    {
        if (synchronous) _source.SetResult(value);
    }
    public void Complete(string value) => _source.SetResult(value);
    public TextAwaiter GetAwaiter() => new(_source.Task);
}

public sealed class NullableTextOperation
{
    private readonly TaskCompletionSource<string> _source = new();
    public NullableTextOperation(string value, bool synchronous)
    {
        if (synchronous) _source.SetResult(value);
    }
    public void Complete(string value) => _source.SetResult(value);
    public NullableTextAwaiter GetAwaiter() => new(_source.Task);
}
