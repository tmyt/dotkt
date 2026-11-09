using System;
using System.Linq;
using System.Reflection;

namespace CustomEvents;

public static class StaticEventEvidence {
    public static bool HasExactStaticShape(object source, string name) {
        var events = source.GetType().Assembly.GetTypes()
            .Select(type => type.GetEvent(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(evt => evt != null).ToArray();
        if (events.Length != 1) return false;
        var evt = events[0];
        var accessors = new[] { evt.AddMethod, evt.RemoveMethod, evt.RaiseMethod };
        return !evt.DeclaringType.IsGenericType && evt.EventHandlerType == typeof(Action<int>)
            && accessors.All(method => method != null && method.IsStatic && !method.IsVirtual && method.IsSpecialName)
            && evt.AddMethod.GetParameters().Single().ParameterType == typeof(Action<int>)
            && evt.RemoveMethod.GetParameters().Single().ParameterType == typeof(Action<int>)
            && evt.RaiseMethod.GetParameters().Single().ParameterType == typeof(int)
            && evt.DeclaringType.GetProperty(name) == null;
    }
}

public class GenericForeignEvents<T> {
    public static event Action<int> ForeignPulse;
    public static void Fire(int value) => ForeignPulse?.Invoke(value);
}
