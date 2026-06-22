// Minimal structured-concurrency surface on the Task sink — the single-shot core of dotktx.coroutines (Path B
// §11/§12: one real scope = Task). `async` starts a child (its kickoff Task) wrapped as a Deferred; `await`
// (a Kotlin suspend fun via the raw intrinsic) suspends on that Task; `runBlocking` drives a root to completion.
// Concurrency falls out of starting both children before awaiting either. (Monomorphic Int — genericity is
// proven separately by il-kgen; full generic facades over generic-typed params are §13g follow-up.)
using System;
using System.Threading.Tasks;

using DotKt;
using DotKt.Coroutines;

namespace DotKtx.Coroutines
{
    public sealed class DeferredI
    {
        public Task<int> Task { get; }
        public DeferredI(Task<int> task) { Task = task; }
    }

    public static class Structured
    {
        public static DeferredI AsyncI(Func<Task<int>> block) => new DeferredI(block());
        public static int RunBlockingI(Func<Task<int>> block) => block().GetAwaiter().GetResult();
    }
}
