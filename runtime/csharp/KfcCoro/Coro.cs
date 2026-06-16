// kotlin/clr coroutine/Task interop runtime (blocking semantics).
// Lets Kotlin suspend functions call real .NET async APIs (Task) and obtain their results.
using System;
using System.Threading.Tasks;

namespace Kfc
{
    // Wraps a real .NET Task<int>; `Value` blocks until it completes (a blocking await).
    public sealed class IntTask
    {
        internal Task<int> Task;
        public int Value => Task.GetAwaiter().GetResult();
    }

    public static class Coro
    {
        // A genuine .NET async operation (Task.Delay) producing a value.
        public static IntTask DelayThenValue(int ms, int value) =>
            new IntTask { Task = System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(ms); return value; }) };

        // runBlocking-style driver: runs a (suspend) lambda to completion.
        public static int Run(Func<int> body) => body();
    }
}
