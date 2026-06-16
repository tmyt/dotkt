// kotlin/clr coroutine/Task interop runtime. Kotlin `suspend` maps to C# `async Task<T>`;
// suspend calls map to `await`, so .NET async APIs are driven NON-BLOCKING from Kotlin.
using System;
using System.Threading.Tasks;

namespace Kfc
{
    public static class Coro
    {
        // A genuine non-blocking async operation: awaits Task.Delay, then yields a value.
        public static async Task<int> FetchValue(int ms, int value)
        {
            await Task.Delay(ms);
            return value;
        }

        public static async Task Delay(int ms) => await Task.Delay(ms);

        // runBlocking-style boundary: drives a suspend (async) lambda to completion.
        public static int Run(Func<Task<int>> body) => body().GetAwaiter().GetResult();
    }
}
