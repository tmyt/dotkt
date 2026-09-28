import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private fun <T> localOverloadSlot(marker: OverloadSlotMarker? = null): OverloadSlotResult<T> =
    OverloadSlotResult(marker, "marker")
private fun <T> localOverloadSlot(value: T): OverloadSlotResult<T> = OverloadSlotResult(null, "value")

private class LocalOverloadSlotFactory<T>(val owner: T) {
    fun <M> select(marker: OverloadSlotMarker?): OverloadSlotResult<T> = OverloadSlotResult(marker, "marker")
    fun <M> select(value: M): OverloadSlotResult<T> = OverloadSlotResult(null, "value")
}

private open class LocalInheritedSlotBase<T>(val owner: T) {
    fun select(marker: OverloadSlotMarker?): OverloadSlotResult<T> = OverloadSlotResult(marker, "marker")
}
private class LocalInheritedSlotDerived<T>(owner: T) : LocalInheritedSlotBase<T>(owner) {
    fun select(value: String): String = value
}
private open class LocalInheritedOverloadBase<T>(val owner: T) {
    fun <M> select(marker: OverloadSlotMarker?): OverloadSlotResult<T> = OverloadSlotResult(marker, "marker")
    fun <M> select(value: M): OverloadSlotResult<T> = OverloadSlotResult(null, "value")
}
private class LocalInheritedOverloadDerived : LocalInheritedOverloadBase<Int>(29)

private suspend fun <T> localSuspendingOverload(marker: OverloadSlotMarker?): OverloadSlotResult<T> =
    OverloadSlotResult(marker, "marker")
private suspend fun <T> localSuspendingOverload(value: T): OverloadSlotResult<T> = OverloadSlotResult(null, "value")

private suspend fun <T> suspendedOverloadSlot(context: CoroutineContext, remote: Boolean): OverloadSlotResult<T> {
    val result = if (remote) remoteOverloadSlot<T>(context[OverloadSlotMarker])
        else localOverloadSlot<T>(context[OverloadSlotMarker])
    suspendCoroutine<Unit> { it.resume(Unit) }
    return result
}

class NullableOverloadSlotTests {
    @TestAttribute
    fun localOverloadsUseSelectedParameterSlots() {
        val marker = OverloadSlotElement()
        val context: CoroutineContext = marker
        val selected = localOverloadSlot<String>(context[OverloadSlotMarker])
        check(selected.marker === marker)
        check(selected.selected == "marker")
        check(localOverloadSlot<Int>(EmptyCoroutineContext[OverloadSlotMarker]).marker == null)
        check(localOverloadSlot<String>("text").selected == "value")
        check(localOverloadSlot<Int>(23).selected == "value")
    }

    @TestAttribute
    fun importedOverloadsKeepSelectionDespiteReverseDeclarationOrder() {
        val marker = OverloadSlotElement()
        val context: CoroutineContext = marker
        val selected = remoteOverloadSlot<String>(context[OverloadSlotMarker])
        check(selected.marker === marker)
        check(selected.selected == "marker")
        check(remoteOverloadSlot<Int>(EmptyCoroutineContext[OverloadSlotMarker]).marker == null)
        check(remoteOverloadSlot<String>("text").selected == "value")
        check(remoteOverloadSlot<Int>(23).selected == "value")
    }

    @TestAttribute
    fun memberOverloadsPreserveOwnerAndMethodFrames() {
        val marker = OverloadSlotElement()
        val context: CoroutineContext = marker
        val local = LocalOverloadSlotFactory(17)
        val remote = RemoteOverloadSlotFactory("owner")
        check(local.select<String>(context[OverloadSlotMarker]).marker === marker)
        check(remote.select<Int>(context[OverloadSlotMarker]).marker === marker)
        check(local.select<String>("value").selected == "value")
        check(remote.select<Int>(23).selected == "value")
        check(local.owner == 17)
        check(remote.owner == "owner")
        val inheritedLocal = LocalInheritedSlotDerived(19)
        val inheritedRemote = RemoteInheritedSlotDerived("remote")
        check(inheritedLocal.select(context[OverloadSlotMarker]).marker === marker)
        check(inheritedRemote.select(context[OverloadSlotMarker]).marker === marker)
        check(inheritedLocal.select(EmptyCoroutineContext[OverloadSlotMarker]).marker == null)
        check(inheritedRemote.select(EmptyCoroutineContext[OverloadSlotMarker]).marker == null)
        check(inheritedLocal.select("local sibling") == "local sibling")
        check(inheritedRemote.select("remote sibling") == "remote sibling")
        check(inheritedLocal.owner == 19)
        check(inheritedRemote.owner == "remote")
        val overloadedLocal = LocalInheritedOverloadDerived()
        val overloadedRemote = RemoteInheritedOverloadDerived()
        check(overloadedLocal.select<String>(context[OverloadSlotMarker]).marker === marker)
        check(overloadedRemote.select<String>(context[OverloadSlotMarker]).marker === marker)
        check(overloadedLocal.select<String>("value").selected == "value")
        check(overloadedRemote.select<Int>(7).selected == "value")
        check(overloadedLocal.owner == 29)
        check(overloadedRemote.owner == 31)
    }

    @TestAttribute
    fun suspendedCallersProjectNullableGenericResultsBeforeArguments() {
        val marker = OverloadSlotElement()
        var completions = 0
        val block: suspend () -> Unit = {
            check(suspendedOverloadSlot<String>(marker, false).marker === marker)
            check(suspendedOverloadSlot<Int>(marker, true).marker === marker)
            check(suspendedOverloadSlot<String>(EmptyCoroutineContext, false).marker == null)
            check(suspendedOverloadSlot<Int>(EmptyCoroutineContext, true).marker == null)
            check(localSuspendingOverload<String>(marker[OverloadSlotMarker]).marker === marker)
            check(remoteSuspendingOverload<Int>(marker[OverloadSlotMarker]).marker === marker)
            check(localSuspendingOverload<Int>(EmptyCoroutineContext[OverloadSlotMarker]).marker == null)
            check(remoteSuspendingOverload<String>(EmptyCoroutineContext[OverloadSlotMarker]).marker == null)
            check(localSuspendingOverload<Int>(7).selected == "value")
            check(remoteSuspendingOverload<String>("value").selected == "value")
        }
        block.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { result.getOrThrow(); completions++ }
        })
        check(completions == 1)
    }
}
