// kotlin/clr coroutine/Task interop. A Kotlin `suspend fun` maps to C# `async Task<T>`, and the
// `@ClrAwait` intrinsic `Task<T>.await()` is the generic bridge to ANY .NET awaitable.
using System;
using System.Threading.Tasks;

namespace Kfc
{
    // An ordinary .NET async API — nothing Kotlin-specific.
    public static class Api
    {
        public static async Task<int> FetchAsync(int ms, int value)
        {
            await Task.Delay(ms);
            return value;
        }
    }

    public static class Coro
    {
        public static int Run(Func<Task<int>> body) => body().GetAwaiter().GetResult();
    }
}
