// Flow (cold, push-based) on the Task foundation — proves Flow needs no new state-machine form: a Flow wraps a
// `suspend (collector) -> …` block; `collect` runs the block with a collector whose `emit` is the consumer action;
// `emit` awaits the action's Task (backpressure = the producer suspends until the collector returns). This is the
// single-shot Task ABI composing into multi-value push. (Int-monomorphic first slice — see docs §13i.)
using System;
using System.Threading.Tasks;

namespace DotKt.Coroutines
{
    /// FlowCollector<Int>: `emit` returns the consumer's Task (awaited by the producer for backpressure).
    public interface FlowColI { Task<int> EmitRaw(int value); }

    /// Flow<Int>: collecting runs the producer block against a collector.
    public sealed class FlowI
    {
        readonly Func<FlowColI, Task<int>> _block;
        public FlowI(Func<FlowColI, Task<int>> block) { _block = block; }
        public Task<int> Collect(FlowColI collector) => _block(collector);
    }

    public static class Flows
    {
        public static FlowI CreateI(Func<FlowColI, Task<int>> block) => new FlowI(block);
        public static Task<int> CollectI(FlowI flow, Func<int, Task<int>> action) => flow.Collect(new ActionCollectorI(action));

        sealed class ActionCollectorI : FlowColI
        {
            readonly Func<int, Task<int>> _action;
            public ActionCollectorI(Func<int, Task<int>> action) { _action = action; }
            public Task<int> EmitRaw(int value) => _action(value);
        }
    }
}

namespace DotKt.Coroutines
{
    // Generic Flow<T> (the real shape). emit returns the consumer action's Task (Int dummy result to dodge Unit
    // in generics for now); the VALUE type T is fully generic.
    public interface FlowCol<T> { Task<int> EmitRaw(T value); }

    public sealed class Flow<T>
    {
        readonly Func<FlowCol<T>, Task<int>> _block;
        public Flow(Func<FlowCol<T>, Task<int>> block) { _block = block; }
        public Task<int> Collect(FlowCol<T> collector) => _block(collector);
    }

    public static class GFlows
    {
        public static Flow<T> Create<T>(Func<FlowCol<T>, Task<int>> block) => new Flow<T>(block);
        public static Task<int> Collect<T>(Flow<T> flow, Func<T, Task<int>> action) => flow.Collect(new AC<T>(action));
        sealed class AC<T> : FlowCol<T>
        {
            readonly Func<T, Task<int>> _action;
            public AC(Func<T, Task<int>> action) { _action = action; }
            public Task<int> EmitRaw(T value) => _action(value);
        }
    }
}
