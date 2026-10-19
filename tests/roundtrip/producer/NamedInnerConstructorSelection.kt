package roundtrip.innerselection

import kotlin.clr.ClrRef
import roundtrip.clrrefparameters.ReferenceBox

class NamedConstructorOuter<out Unrelated, T> {
    inner class Entry {
        val selection: Int
        constructor(value: T) { selection = 1 }
        constructor(text: String) { selection = 2 }
    }
    inner class AnyEntry {
        val selection: Int
        constructor(value: T) { selection = 1 }
        constructor(any: Any?) { selection = 2 }
    }
    inner class NullableEntry {
        val selection: Int
        constructor(value: T) { selection = 1 }
        constructor(nullable: T?) { selection = 2 }
    }
    inner class GenericEntry<U> {
        val selection: Int
        constructor(value: T, other: U) { selection = 1 }
        constructor(text: String, other: U) { selection = 2 }
    }
    inner class ReferenceEntry {
        val selection: Int
        constructor(value: ClrRef<ReferenceBox<T>>, replacement: ReferenceBox<T>) {
            value.value = replacement
            selection = 1
        }
        constructor(text: ClrRef<ReferenceBox<String>>, replacement: ReferenceBox<String>) {
            text.value = replacement
            selection = 2
        }
    }
}

fun sameModuleNamedConstructorSelection(): Boolean {
    val outer: NamedConstructorOuter<Any, String> = NamedConstructorOuter<Int, String>()
    return outer.Entry(value = "x").selection == 1 && outer.Entry(text = "x").selection == 2
        && outer.AnyEntry(value = "x").selection == 1 && outer.AnyEntry(any = null).selection == 2
        && outer.NullableEntry(value = "x").selection == 1 && outer.NullableEntry(nullable = null).selection == 2
        && outer.GenericEntry<Int>(value = "x", other = 42).selection == 1
        && outer.GenericEntry<Int>(text = "x", other = 42).selection == 2
}

fun <T> sameModuleGenericConstructorSelection(outer: NamedConstructorOuter<Any, T>, value: T): Int =
    outer.Entry(value = value).selection + outer.GenericEntry<Int>(value = value, other = 42).selection
