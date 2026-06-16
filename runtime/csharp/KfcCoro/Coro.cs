// kotlin/clr coroutine/Task interop. Kotlin `suspend` -> C# `async Task<T>`; the @ClrAwait
// intrinsic `Task<T>.await()` is the generic bridge to ANY .NET awaitable.
using System;
using System.Threading.Tasks;

namespace Kfc
{
    public static class Api
    {
        public static async Task<int> FetchAsync(int ms, int value)
        {
            await Task.Delay(ms);
            return value;
        }

        public static async Task<int> FailAsync()
        {
            await Task.Delay(5);
            throw new InvalidOperationException("boom");
        }
    }

    public static class Coro
    {
        public static int Run(Func<Task<int>> body) => body().GetAwaiter().GetResult();
    }
}
