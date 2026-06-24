namespace Kfc {
    public static class Api {
        // A .NET async stream (IAsyncEnumerable<int>) — the source we bridge into a Kotlin Flow.
        public static async System.Collections.Generic.IAsyncEnumerable<int> Range(int n) {
            for (int i = 0; i < n; i++) { await System.Threading.Tasks.Task.Yield(); yield return i; }
        }
    }
}
