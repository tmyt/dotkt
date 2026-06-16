// REFERENCE + TEST: the exact state-machine shape the suspend lowering (D2.1) must generate,
// hand-written to validate the D2.0 runtime drives real suspension/resumption end-to-end.
//
// Models:  suspend fun chain(): Int { val a = step(10); val b = step(20); return a + b }
// where `step` is a real .NET async source (Task<int>). No C# async/await is used in the
// coroutine itself — the engine (IContinuation + Future + TCS) does the suspension, proving
// the pure-Kotlin-runtime path (strategy B), not strategy A.
using System;
using System.Threading.Tasks;
using Kotlin.Coroutines;

static class StateMachineRef
{
    // A real async value source (the .NET side).
    static Task<int> Step(int v) => Task.Run(async () => { await Task.Delay(20); return v; });

    // The compiler-generated state machine for `chain` would look like this:
    sealed class ChainCoroutine : IContinuation<int>
    {
        int _label;
        int _a;
        readonly IContinuation<int> _completion;
        public CoroutineContext Context => _completion.Context;
        public ChainCoroutine(IContinuation<int> completion) { _completion = completion; }

        public void ResumeWith(KResult<object> result)
        {
            try
            {
                switch (_label)
                {
                    case 0:
                        _label = 1;
                        if (Suspend(Step(10))) return;   // suspends; resumes at label 1
                        goto case 1;
                    case 1:
                        _a = (int)result.Value;
                        _label = 2;
                        if (Suspend(Step(20))) return;   // suspends; resumes at label 2
                        goto case 2;
                    case 2:
                        int b = (int)result.Value;
                        _completion.ResumeWith(KResult<object>.Success(_a + b)); // -> TCS.SetResult
                        return;
                }
            }
            catch (Exception e) { _completion.ResumeWith(KResult<object>.Fail(e)); } // -> TCS.SetException
        }

        // Attach this coroutine as the awaitable's continuation (the suspension mechanism).
        bool Suspend(Task<int> awaitable)
        {
            awaitable.GetAwaiter().OnCompleted(() =>
                ResumeWith(awaitable.IsFaulted
                    ? KResult<object>.Fail(awaitable.Exception.InnerException)
                    : KResult<object>.Success(awaitable.Result)));
            return true; // always suspends (the awaitable isn't complete yet)
        }
    }

    static int Main()
    {
        // Public bridge: `Task<int> Chain()` = Future(ctx, start), Continuation hidden.
        Task<int> Chain() => CoroutineBuilders.Future<int>(
            CoroutineContext.Empty,
            root => new ChainCoroutine(root).ResumeWith(KResult<object>.Success(null)));

        var result = Chain().GetAwaiter().GetResult();
        Console.WriteLine($"chain = {result}");
        return result == 30 ? 0 : 1;
    }
}
