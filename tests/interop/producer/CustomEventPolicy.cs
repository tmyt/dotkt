using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CustomEvents;

public static class PolicyEvidence {
    public static bool IsNativeEvent(object source, string name) {
        var type = source.GetType();
        var evt = type.GetEvent(name);
        return evt?.AddMethod?.IsSpecialName == true && evt.RemoveMethod?.IsSpecialName == true
            && evt.RaiseMethod?.IsSpecialName == true && type.GetProperty(name) == null;
    }
    public static bool IsOrdinaryProperty(object source, string name) =>
        source.GetType().GetProperty(name) != null && source.GetType().GetEvent(name) == null;

    private sealed class Subscriber { public void Handle(object value) { } }

    // Keep both the subscriber and its delegate out of the caller's JIT lifetime. The callback must not retain
    // the returned subscription token: a live token intentionally owns its exact handler strongly.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference RegisterCollectible(Action<Action<object>> register) {
        var subscriber = new Subscriber();
        register(subscriber.Handle);
        return new WeakReference(subscriber);
    }
    public static bool Collect(WeakReference weak) {
        for (var i = 0; i < 8 && weak.IsAlive; i++) {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        return !weak.IsAlive;
    }
}
