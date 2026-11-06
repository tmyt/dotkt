using System;
using System.Runtime.CompilerServices;

namespace DotKt.Tests.Suppression;

public static class SuppressionGcProbe
{
    // Finish the frame holding the only strong references before the caller collects.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference[] CreateCycle(Action<Exception, Exception> suppress)
    {
        var primary = new InvalidOperationException("primary");
        var child = new ArgumentException("child", primary);
        suppress(primary, child);
        return new[] { new WeakReference(primary), new WeakReference(child) };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsDead(WeakReference reference) => !reference.IsAlive;

    public static bool AreCollected(WeakReference[] references)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // Do not keep temporary weak targets in this frame across the next GC.
            if (IsDead(references[0]) && IsDead(references[1])) return true;
        }
        return false;
    }
}
