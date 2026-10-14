import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.overloadpriority.*

private fun priorityResult(block: suspend () -> String): String {
    var value: String? = null
    var failure: Throwable? = null
    var done = false
    block.startCoroutine(object : Continuation<String> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<String>) {
            value = result.getOrNull()
            failure = result.exceptionOrNull()
            done = true
        }
    })
    check(done)
    failure?.let { throw it }
    return value!!
}

class OverloadPriorityTests {
    @TestAttribute
    fun aliasFactoryAndConstructorKeepSourcePriority() {
        check(sourceAlias() == "constructor")
        check(Alias("message", null).selected == sourceAlias())
    }

    @TestAttribute
    fun ordinaryGenericReceiverAndUnannotatedControlsKeepSourceSelection() {
        check(sourcePick() == "normal" && pick("value") == sourcePick())
        check(sourceControl() == "specific" && control("value") == sourceControl())
        check(sourceGeneric() == "normal" && genericPick(7, "value") == sourceGeneric())
        check(sourceExtension() == "normal" && "receiver".extensionPick("value") == sourceExtension())
    }

    @TestAttribute
    fun membersModifiersAndInheritedDefaultDeclarationsKeepSourceSelection() {
        val imported = Members().memberPick("value") + (Members() join "value") + (Members() + "value") +
            with(Members()) { "receiver".memberExtension("value") } + Inherited().inheritedPick("value")
        check(sourceMembers() == "normalnormalnormalnormalnormal")
        check(imported == sourceMembers())
    }

    @TestAttribute
    fun companionCarriersAndGenericCompanionExtensionCoresKeepSourceSelection() {
        val imported = StaticHost.staticPick("value") + GenericHost.staticPick("value") +
            StaticHost.companionPick("value") + GenericHost.companionPick("value")
        check(sourceStatics() == "normalnormalnormalnormal")
        check(imported == sourceStatics())
    }

    @TestAttribute
    fun abstractAndConcreteSuspendBridgesComposePriorityWithSourceModifiers() {
        check(priorityResult { sourceSuspended() } == "normalnormal")
        check(priorityResult { suspendedPick("value") + SuspendImplementation()("value") } == "normalnormal")
        val concrete = SuspendImplementation()
        val abstract: SuspendSlot = concrete
        check(priorityResult { concrete.onlyLow("concrete") + abstract.onlyLow("abstract") } == "concreteabstract")
    }
}
