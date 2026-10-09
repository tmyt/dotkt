@file:Suppress("UNCHECKED_CAST")
package roundtriptests.sharedcellcarrier

import NUnit.Framework.TestAttribute
import roundtrip.sharedcellcarrier.*

class LocalTagBox<T>(val tag: String)
open class ParentTagBox<T>(val tag: String)
class ChildTagBox<T>(tag: String) : ParentTagBox<T>(tag)

private fun <T> replaceBaseCaptured(raw: Any): String {
    var current: ParentTagBox<T> = ChildTagBox<T>("initial")
    val update = { current = raw as ChildTagBox<T> }
    update()
    check((current as Any) === raw)
    return current.tag
}

private fun <T> readInitializedCell(raw: Any): String {
    var current = raw as LocalTagBox<T>
    val read = {
        check((current as Any) === raw)
        current.tag
    }
    return read()
}

private fun <T> takeExact(value: LocalTagBox<T>): String = value.tag

private fun <T> exactOnlyCell(): String {
    var current = LocalTagBox<T>("initial")
    val update = { current = LocalTagBox<T>("changed") }
    update()
    return takeExact(current)
}

private fun <T> exactMixedReturn(useCell: Boolean): LocalTagBox<T> {
    var current = LocalTagBox<T>("initial")
    val update = { current = LocalTagBox<T>("changed") }
    update()
    if (useCell) return current
    return LocalTagBox<T>("fresh")
}

private fun <T> replaceCaptured(raw: Any): String {
    var current = LocalTagBox<T>("initial")
    val update = { current = raw as LocalTagBox<T> }
    update()
    check((current as Any) === raw)
    return current.tag
}

private fun <T> replaceNullable(raw: Any, updateValue: Boolean): String? {
    var current: LocalTagBox<T>? = null
    val update = { current = raw as LocalTagBox<T> }
    if (updateValue) update()
    return current?.tag
}

private fun <A, T> readShared(raw: Any, marker: A): String {
    var current = LocalTagBox<T>("initial")
    val read = {
        check(marker != null)
        check((current as Any) === raw)
        current.tag
    }
    val update = { current = raw as LocalTagBox<T> }
    update()
    return read()
}

private fun <T> replaceImported(): String {
    val raw = importedRaw()
    var current = ImportedTagBox<T>("initial")
    val update = { current = raw as ImportedTagBox<T> }
    update()
    check((current as Any) === raw)
    return current.tag
}

class SharedCellCarrierTests {
    @TestAttribute
    fun projectedInitializerIsACellWrite() {
        check(readInitializedCell<String>(LocalTagBox<Int>("raw")) == "raw")
    }

    @TestAttribute
    fun exactWritesKeepExactArgumentSlots() {
        check(exactOnlyCell<String>() == "changed")
    }

    @TestAttribute
    fun exactWritesKeepMixedReturnSlots() {
        check(exactMixedReturn<String>(true).tag == "changed")
        check(exactMixedReturn<String>(false).tag == "fresh")
    }

    @TestAttribute
    fun derivedCarrierCanFillABaseCell() {
        check(replaceBaseCaptured<String>(ChildTagBox<Int>("derived")) == "derived")
    }

    @TestAttribute
    fun capturedGenericAssignmentPreservesRawIdentity() {
        check(replaceCaptured<String>(LocalTagBox<Int>("integer")) == "integer")
        check(replaceCaptured<Int>(LocalTagBox<String>("string")) == "string")
    }

    @TestAttribute
    fun nullableCellKeepsNullAndProjectsTheNonNullRead() {
        val raw = LocalTagBox<Int>("nullable")
        check(replaceNullable<String>(raw, false) == null)
        check(replaceNullable<String>(raw, true) == "nullable")
    }

    @TestAttribute
    fun twoClosuresShareTheCellAndPreserveItsGenericFrame() {
        check(readShared<Int, String>(LocalTagBox<Int>("shared"), 17) == "shared")
    }

    @TestAttribute
    fun referencedGenericClassifierUsesTheSameCellContract() {
        check(replaceImported<String>() == "foreign")
    }
}
