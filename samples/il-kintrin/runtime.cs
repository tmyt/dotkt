using System;
using System.Threading.Tasks;
namespace Kfc {
    public static class Api2 {
        public static Task<int> Step(int v) => Task.Run(async () => { await Task.Delay(15); return v; });
    }
    public static class Coro {
        public static int Run(Func<Task<int>> body) => body().GetAwaiter().GetResult();
    }
}
