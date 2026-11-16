using System;

namespace EventDelegation {
    public class NativeEventBase<T> {
        public virtual event Action<T> Changed;
        public int BaseCount => Changed?.GetInvocationList().Length ?? 0;
    }

    public abstract class AbstractEventBase<T> {
        public abstract event Action<T> Changed;
    }

    public abstract class AbstractEventMiddle<T> : AbstractEventBase<T> { }

    public interface IEventSource<T> {
        event Action<T> Changed;
        void Fire(T value);
    }

    // The delegated interface inherits the event slot from a different generic interface. This catches both
    // reflection's non-inherited interface-event lookup and composition of the constructed T across that edge.
    public interface IInheritedEventSource<T> : IEventSource<T> { }

    public static class DefaultEventProbe {
        public static int Added, Removed, GenericAdded, GenericRemoved;
        public static bool DeclaresOwnEvent(object value) {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.DeclaredOnly;
            var type = value.GetType();
            foreach (var ev in type.GetEvents(flags))
                if (ev.Name == "Changed") return true;
            foreach (var field in type.GetFields(flags))
                if (typeof(Delegate).IsAssignableFrom(field.FieldType)) return true;
            return false;
        }
    }

    public interface IDefaultEvent {
        event EventHandler Changed {
            add { DefaultEventProbe.Added++; value?.Invoke(this, EventArgs.Empty); }
            remove { DefaultEventProbe.Removed++; }
        }
    }

    public interface IGenericDefaultEvent<T> {
        event Action<T> Changed {
            add { DefaultEventProbe.GenericAdded++; }
            remove { DefaultEventProbe.GenericRemoved++; }
        }
    }

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
