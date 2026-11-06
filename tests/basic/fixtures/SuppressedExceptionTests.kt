import NUnit.Framework.TestAttribute

private class SuppressionEqualException : RuntimeException() {
    override fun equals(other: Any?): Boolean = other is SuppressionEqualException
    override fun hashCode(): Int = 1
}

class SuppressedExceptionTests {
    @TestAttribute
    fun directSuppressionPreservesIdentityOrderDuplicatesAndSnapshots() {
        val primary = IllegalStateException("primary")
        val first = IllegalArgumentException("first")
        val second = RuntimeException("second")
        val empty = primary.suppressedExceptions
        primary.addSuppressed(first)
        val one = primary.suppressedExceptions
        primary.addSuppressed(primary)
        primary.addSuppressed(second)
        primary.addSuppressed(first)
        val all = primary.suppressedExceptions
        check(empty.isEmpty())
        check(one.size == 1 && one[0] === first)
        check(all.size == 3)
        check(all[0] === first && all[1] === second && all[2] === first)
        check(RuntimeException("other").suppressedExceptions.isEmpty())
    }

    @TestAttribute
    fun nestedUseRetainsThePrimaryAndCloseFailureOrder() {
        val primary = IllegalStateException("body")
        val innerFailure = IllegalArgumentException("inner")
        val outerFailure = RuntimeException("outer")
        var innerCloses = 0
        var outerCloses = 0
        val outer = AutoCloseable { outerCloses++; throw outerFailure }
        val inner = AutoCloseable { innerCloses++; throw innerFailure }
        val caught = try {
            outer.use { inner.use { throw primary } }
        } catch (failure: Throwable) { failure }
        check(caught === primary)
        check(innerCloses == 1 && outerCloses == 1)
        val suppressed = caught.suppressedExceptions
        check(suppressed.size == 2)
        check(suppressed[0] === innerFailure && suppressed[1] === outerFailure)
    }

    @TestAttribute
    fun closeOnlyAndNullResourcesDoNotInventSuppression() {
        val closeFailure = IllegalArgumentException("close only")
        val caught = try {
            AutoCloseable { throw closeFailure }.use { 42 }
            null
        } catch (failure: Throwable) { failure }
        check(caught === closeFailure)
        check(closeFailure.suppressedExceptions.isEmpty())

        val absent: AutoCloseable? = null
        check(absent.use { it == null })
        val bodyFailure = IllegalStateException("no resource")
        val body = try { absent.use { throw bodyFailure } } catch (failure: Throwable) { failure }
        check(body === bodyFailure && body.suppressedExceptions.isEmpty())
    }

    @TestAttribute
    fun storageUsesIdentityRatherThanOverriddenEquality() {
        val first = SuppressionEqualException()
        val second = SuppressionEqualException()
        check(first == second && first !== second)
        val firstChild = RuntimeException("first child")
        val secondChild = RuntimeException("second child")
        first.addSuppressed(firstChild)
        second.addSuppressed(secondChild)
        check(first.suppressedExceptions.single() === firstChild)
        check(second.suppressedExceptions.single() === secondChild)
    }

    @TestAttribute
    fun concurrentAddsAndSnapshotsRetainEveryException() {
        val primary = RuntimeException("concurrent")
        val children = Array(4) { worker -> Array(32) { index -> RuntimeException("$worker/$index") } }
        val failures = arrayOfNulls<Throwable>(4)
        val ready = System.Threading.CountdownEvent(4)
        val start = System.Threading.ManualResetEventSlim(false)
        val threads = Array(4) { worker ->
            System.Threading.Thread(System.Threading.ThreadStart {
                try {
                    ready.Signal()
                    check(start.Wait(10_000))
                    for (child in children[worker]) {
                        primary.addSuppressed(child)
                        check(primary.suppressedExceptions.any { it === child })
                    }
                } catch (failure: Throwable) { failures[worker] = failure }
            })
        }
        for (thread in threads) { thread.IsBackground = true; thread.Start() }
        try {
            check(ready.Wait(10_000))
            start.Set()
            for (thread in threads) check(thread.Join(10_000))
            check(failures.all { it == null })
            val all = primary.suppressedExceptions
            check(all.size == 128)
            for (worker in children) {
                var previous = -1
                for (child in worker) {
                    check(all.count { it === child } == 1)
                    val index = all.indexOfFirst { it === child }
                    check(index > previous)
                    previous = index
                }
            }
        } finally {
            start.Set()
            for (thread in threads) if (thread.IsAlive) thread.Join(10_000)
            start.Dispose()
            ready.Dispose()
        }
    }
}
