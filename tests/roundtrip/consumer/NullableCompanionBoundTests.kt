package roundtrip.nullablecompanion

import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

private fun <U, T : U> localCompanionWiden(value: T): U = value
private fun <T> localCompanionNullable(value: T): T? = localCompanionWiden<T?, T>(value)
private class ImportedCompanionSink : CompanionSinkBase(), CompanionSink
private class ImportedBoundedCompanionSink : BoundedCompanionSinkBase(), BoundedCompanionSink
private open class LocalCompanionSinkBase { fun <T> count(values: List<T?>): Int = values.size }
private class LocalBaseImportedCompanionSink : LocalCompanionSinkBase(), CompanionSink
private class CompanionArgumentOuter { class Entry }
private open class CompanionArgumentBase { fun count(value: CompanionArgumentOuter.Entry): Int = 1 }
private class ImportedNestedArgumentSink : CompanionArgumentBase(), CompanionArgumentSink<CompanionArgumentOuter.Entry>
private interface ConsumerCompanionSink { fun <T> count(values: List<T?>): Int }
private class ImportedBaseConsumerSink : CompanionSinkBase(), ConsumerCompanionSink
private interface ConsumerBoundedCompanionSink { fun <T : CompanionNamed> count(values: List<T?>): Int }
private class ImportedBaseConsumerBoundedSink : BoundedCompanionSinkBase(), ConsumerBoundedCompanionSink
private interface ConsumerOwnerCompanionSink<Root : CompanionNamed> { fun <T : Root> count(values: List<T?>): Int }
private class ImportedBaseConsumerOwnerSink<Unused, Root : CompanionNamed> :
    OwnerBoundedCompanionSinkBase<Root>(), ConsumerOwnerCompanionSink<Root>
private class ImportedDefaultCompanionSink : DefaultCompanionSink
private class CompanionCompletion<T> : Continuation<T> {
    var completed = false
    var value: T? = null
    override val context = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) { value = result.getOrThrow(); completed = true }
}
private fun <T> runCompanion(block: suspend () -> T): T {
    val completion = CompanionCompletion<T>()
    block.startCoroutine(completion)
    check(!completion.completed)
    resumeCompanion()
    check(completion.completed)
    @Suppress("UNCHECKED_CAST")
    return completion.value as T
}

class NullableCompanionBoundTests {
    @TestAttribute
    fun importedBaseFillsLocalNullableGenericContracts() {
        val unbounded: ConsumerCompanionSink = ImportedBaseConsumerSink()
        check(unbounded.count(listOf<Int?>(1, null)) == 2)
        check(unbounded.count(listOf<String?>("reference", null)) == 2)
        val values = listOf<CompanionName?>(CompanionName("bound"), null)
        val bounded: ConsumerBoundedCompanionSink = ImportedBaseConsumerBoundedSink()
        check(bounded.count(values) == 2)
        val owner: ConsumerOwnerCompanionSink<CompanionName> = ImportedBaseConsumerOwnerSink<String, CompanionName>()
        check(owner.count(values) == 2)
    }

    @TestAttribute
    fun inheritedImplementationsCompareSourceBounds() {
        val values = listOf<Int?>(1, null)
        val local: CompanionSink = LocalCompanionSink()
        check(local.count(values) == 2)
        val imported: CompanionSink = ImportedCompanionSink()
        check(imported.count(values) == 2)
        check(imported.count(listOf<String?>("reference", null)) == 2)
        val bounded = listOf<CompanionName?>(CompanionName("bound"), null)
        val localBounded: BoundedCompanionSink = LocalBoundedCompanionSink()
        check(localBounded.count(bounded) == 2)
        val importedBounded: BoundedCompanionSink = ImportedBoundedCompanionSink()
        check(importedBounded.count(bounded) == 2)
        val localBase: CompanionSink = LocalBaseImportedCompanionSink()
        check(localBase.count(values) == 2)
        val nested: CompanionArgumentSink<CompanionArgumentOuter.Entry> = ImportedNestedArgumentSink()
        check(nested.count(CompanionArgumentOuter.Entry()) == 1)
        val localDefault: DefaultCompanionSink = LocalDefaultCompanionSink()
        val importedDefault: DefaultCompanionSink = ImportedDefaultCompanionSink()
        check(localDefault.count(values) == 2)
        check(importedDefault.count(values) == 2)
    }

    @TestAttribute
    fun localAndImportedMethodsRetainNullableWidening() {
        check(localCompanionNullable(17) == 17)
        check(localCompanionNullable("local") == "local")
        check(companionNullable(23) == 23)
        check(companionNullable("imported") == "imported")
        check(companionNullable<Int?>(null) == null)
        check(companionNullable<Int?>(0) == 0)
        check(companionNullable<String?>(null) == null)
        check(VirtualCompanion().get(71) == 71)
    }

    @TestAttribute
    fun importedOwnersAndClosuresKeepSourceBounds() {
        check(companionDeferred(31)() == 31)
        check(companionDeferred("closure")() == "closure")
        check(companionDeferred<Int?>(null)() == null)
        check(companionBox(41).nullable() == 41)
        check(companionBox("owner").deferred()() == "owner")
        check(companionBox<Int?>(null).nullable() == null)
        val named = CompanionName("bound")
        check(BoundedCompanionBox(named).nullable() === named)
        check(BoundedCompanionBox(named).nullable()!!.name == "bound")
        val variant: VariantCompanion<Int> = VariantCompanionImpl(61)
        check(variant.get() == 61)
        val inner = OuterCompanion(67).Inner("inner")
        check(inner.outer() == 67)
        check(inner.inner() == "inner")
    }

    @TestAttribute
    fun importedSuspendFramesKeepNullableWidening() {
        check(runCompanion { companionSuspended(53) } == 53)
        check(runCompanion { companionSuspended("resumed") } == "resumed")
        check(runCompanion { companionSuspended<Int?>(null) } == null)
        check(runCompanion(capturedCompanion(59)) == 59)
        check(runCompanion(capturedCompanion("suspend capture")) == "suspend capture")
        check(runCompanion(capturedCompanion<Int?>(null)) == null)
    }
}
