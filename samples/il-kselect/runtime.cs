namespace Kfc {
    public static class Api {
        // A value that arrives after `ms` — the awaitable a select clause races on.
        public static async System.Threading.Tasks.Task<int> Delayed(int value, int ms) {
            await System.Threading.Tasks.Task.Delay(ms);
            return value;
        }
    }
}
