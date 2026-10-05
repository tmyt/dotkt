import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

private class CovariantContextCompletion<T> : Continuation<T> {
    override val context = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) {}
}

private open class InheritedContextBase<in T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) {}
}
private class InheritedContextChild<U, in T : U> : InheritedContextBase<T>()
private fun <U, T : U> inheritedContext(value: InheritedContextChild<U, T>): CoroutineContext = value.context
private var overriddenContextReads = 0
private open class OverriddenContextMiddle<in T> : InheritedContextBase<T>() {
    override val context: CoroutineContext get() {
        overriddenContextReads++
        return EmptyCoroutineContext
    }
}
private class OverriddenContextLeaf<U, in T : U> : OverriddenContextMiddle<T>()
private fun <U, T : U> overriddenContext(value: OverriddenContextLeaf<U, T>): CoroutineContext = value.context

private interface ContextCell<T> { var item: String }
private interface LeftContextCell<T> : ContextCell<T>
private interface RightContextCell<T> : ContextCell<T>
private open class ContextCellBase<T>(initial: String) : LeftContextCell<T> {
    private var stored = initial
    var writes = 0
    override var item: String
        get() = stored
        set(value) { writes++; stored = value }
}
private class DiamondContextCell<U, T : U>(initial: String) : ContextCellBase<T>(initial), RightContextCell<T>
private var cellReceiverEvaluations = 0
private var cellValueEvaluations = 0
private fun <U, T : U> selectCell(cell: DiamondContextCell<U, T>): DiamondContextCell<U, T> {
    cellReceiverEvaluations++
    return cell
}
private fun selectCellValue(value: String): String { cellValueEvaluations++; return value }
private fun <U, T : U> updateCell(cell: DiamondContextCell<U, T>, value: String): String {
    selectCell(cell).item = selectCellValue(value)
    return cell.item
}

private interface CovariantContextSlot<T> { val context: CoroutineContext }
private class NestedContinuationContext : CovariantContextSlot<List<Continuation<Int>>> {
    override val context = EmptyCoroutineContext
}
private class NestedResultContext : CovariantContextSlot<List<Result<Int>>> {
    override val context = EmptyCoroutineContext
}
private open class CovariantCompletionBase<T> { open fun completion(): T? = null }
private class NarrowCompletionBase : CovariantCompletionBase<Continuation<Int>>() {
    override fun completion(): Continuation<Int> = CovariantContextCompletion<Int>()
}

class CovariantContinuationContextTests {
    @TestAttribute
    fun inheritedPropertyDiamondPreservesSetterEvaluation() {
        cellReceiverEvaluations = 0
        cellValueEvaluations = 0
        val integers = DiamondContextCell<Any?, Int>("before")
        check(updateCell(integers, "integer cell") == "integer cell")
        check(integers.writes == 1)
        check(cellReceiverEvaluations == 1)
        check(cellValueEvaluations == 1)
        val strings = DiamondContextCell<Any?, String>("before")
        check(updateCell(strings, "string cell") == "string cell")
        check(strings.writes == 1)
        check(cellReceiverEvaluations == 2)
        check(cellValueEvaluations == 2)
    }

    @TestAttribute
    fun constructedBaseAndNestedInterfaceOwnersUseTheSameRepresentation() {
        val continuationSlot: CovariantContextSlot<List<Continuation<Int>>> = NestedContinuationContext()
        val resultSlot: CovariantContextSlot<List<Result<Int>>> = NestedResultContext()
        check(continuationSlot.context === EmptyCoroutineContext)
        check(resultSlot.context === EmptyCoroutineContext)
        val base: CovariantCompletionBase<Continuation<Int>> = NarrowCompletionBase()
        check(base.completion()!!.context === EmptyCoroutineContext)
    }

    @TestAttribute
    fun anonymousContextDispatchesThroughTheErasedInterface() {
        val integers = object : Continuation<Int> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {}
        }
        val strings = object : Continuation<String> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<String>) {}
        }
        check(integers.context === EmptyCoroutineContext)
        check(strings.context === EmptyCoroutineContext)
        val integerInterface: Continuation<Int> = integers
        val stringInterface: Continuation<String> = strings
        check(integerInterface.context === EmptyCoroutineContext)
        check(stringInterface.context === EmptyCoroutineContext)
        integerInterface.resumeWith(Result.success(7))
        stringInterface.resumeWith(Result.success("ok"))
    }

    @TestAttribute
    fun namedGenericContextDispatchesThroughTheErasedInterface() {
        check(inheritedContext(InheritedContextChild<Any?, String>()) === EmptyCoroutineContext)
        check(inheritedContext(InheritedContextChild<Any?, Int>()) === EmptyCoroutineContext)
        overriddenContextReads = 0
        check(overriddenContext(OverriddenContextLeaf<Any?, String>()) === EmptyCoroutineContext)
        check(overriddenContextReads == 1)
        val integers = CovariantContextCompletion<Int>()
        val strings = CovariantContextCompletion<String>()
        check(integers.context === EmptyCoroutineContext)
        check(strings.context === EmptyCoroutineContext)
        val integerInterface: Continuation<Int> = integers
        val stringInterface: Continuation<String> = strings
        val context: CoroutineContext = integerInterface.context
        check(context === EmptyCoroutineContext)
        check(stringInterface.context === EmptyCoroutineContext)
        integerInterface.resumeWith(Result.success(9))
        stringInterface.resumeWith(Result.success("named"))
    }
}
