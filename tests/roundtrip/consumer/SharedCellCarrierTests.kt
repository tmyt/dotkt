@file:Suppress("UNCHECKED_CAST")
package roundtriptests.sharedcellcarrier

import NUnit.Framework.TestAttribute
import roundtrip.sharedcellcarrier.*

class LocalTagBox<T>(val tag: String)

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
