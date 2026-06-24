using System;
using System.Threading.Tasks;
namespace Kfc {
    // A stand-in async data source (returns a genuinely-incomplete Task<int> so suspension really happens).
    public static class Api2 {
        public static Task<int> Step(int v) => Task.Run(async () => { await Task.Delay(15); return v; });
        // A genuinely-incomplete Task<int> that FAULTS after suspending — exercises try/catch-around-await.
        public static Task<int> Boom(int v) => Task.Run<int>(async () => { await Task.Delay(15); throw new InvalidOperationException("boom " + v); });
    }
    // The coroutine builder boundary: run a `suspend ()->Int` (a Func<Task<int>> in the CLR ABI) to completion.
    public static class Coro {
        public static int Run(Func<Task<int>> body) => body().GetAwaiter().GetResult();
    }
}
