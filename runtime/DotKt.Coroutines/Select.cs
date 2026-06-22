// kotlinx.coroutines `select { … }` modeled on the Task ABI (T9). A select registers clauses — each a Task plus a
// suspend handler — and resumes with the handler result of whichever Task completes first (Task.WhenAny). No new
// compiler machinery: the `select { onAwait(t) { … } }` block is a receiver lambda (T11) whose body registers
// clauses via a member call, and the handlers are suspend lambdas — both already supported.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DotKt.Coroutines
{
    public sealed class Selector<R>
    {
        readonly List<(Task task, Func<Task<R>> run)> _clauses = new List<(Task, Func<Task<R>>)>();

        /// Register a clause: when `task` is the first to complete, run `handler(task.Result)` for the select result.
        public void OnAwait<T>(Task<T> task, Func<T, Task<R>> handler) =>
            _clauses.Add((task, () => handler(task.Result)));

        public async Task<R> RunAsync()
        {
            var tasks = _clauses.Select(c => c.task).ToArray();
            var winner = await Task.WhenAny(tasks);
            return await _clauses[Array.IndexOf(tasks, winner)].run();
        }
    }

    public static class Selectors
    {
        /// Build the selector by running the (clause-registering) block, then await the first-ready clause. The block
        /// is a receiver lambda `Selector<R>.() -> Int` (the Int result is a dummy — emitted as Func`2, like Flow).
        public static Task<R> Select<R>(Func<Selector<R>, int> block)
        {
            var s = new Selector<R>();
            block(s);
            return s.RunAsync();
        }
    }
}
