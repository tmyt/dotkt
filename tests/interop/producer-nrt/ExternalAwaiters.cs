using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ExternalAwaiterContracts;

public readonly struct OptionalAwaiter<T> : INotifyCompletion where T : class
{
    private readonly Task<T?> _task;
    public OptionalAwaiter(Task<T?> task) => _task = task;
    public bool IsCompleted => _task.IsCompleted;
    public T? GetResult() => _task.GetAwaiter().GetResult();
    public void OnCompleted(Action continuation) => _task.GetAwaiter().OnCompleted(continuation);
}

public readonly struct TextAwaiter : INotifyCompletion
{
    private readonly Task<string> _task;
    public TextAwaiter(Task<string> task) => _task = task;
    public bool IsCompleted => _task.IsCompleted;
    public string GetResult() => _task.GetAwaiter().GetResult();
    public void OnCompleted(Action continuation) => _task.GetAwaiter().OnCompleted(continuation);
}

public readonly struct NullableTextAwaiter : INotifyCompletion
{
    private readonly Task<string?> _task;
    public NullableTextAwaiter(Task<string?> task) => _task = task;
    public bool IsCompleted => _task.IsCompleted;
    public string? GetResult() => _task.GetAwaiter().GetResult();
    public void OnCompleted(Action continuation) => _task.GetAwaiter().OnCompleted(continuation);
}
