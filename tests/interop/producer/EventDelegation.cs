using System;

namespace EventDelegation {
    public interface IEventSource<T> {
        event Action<T> Changed;
        void Fire(T value);
    }

    // The delegated interface inherits the event slot from a different generic interface. This catches both
    // reflection's non-inherited interface-event lookup and composition of the constructed T across that edge.
    public interface IInheritedEventSource<T> : IEventSource<T> { }

    public sealed class EventSource<T> : IInheritedEventSource<T> {
        private Action<T> _changed;
        public int AddCount { get; private set; }
        public int RemoveCount { get; private set; }
        public event Action<T> Changed {
            add { AddCount++; _changed += value; }
            remove { RemoveCount++; _changed -= value; }
        }
        public void Fire(T value) => _changed?.Invoke(value);
    }
}
