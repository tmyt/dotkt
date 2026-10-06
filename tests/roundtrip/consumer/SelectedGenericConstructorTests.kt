package roundtriptests.constructorselection

import NUnit.Framework.TestAttribute
import roundtrip.constructorselection.*

fun <T> checkImportedGenericSelection(input: T) {
    check(SelectedConstructor<T>(value = input).chosen == 1)
    check(SelectedConstructor<T>(marker = 61).chosen == 2)
}

class SelectedGenericConstructorTests {
    @TestAttribute
    fun localDeclarationsRemainDistinctAfterInstantiation() {
        checkLocalConstructorSelection()
    }

    @TestAttribute
    fun importedDeclarationsRemainDistinctAfterInstantiation() {
        check(StarSelectedConstructor(star = SelectionBox("star")).chosen == 1)
        check(StarSelectedConstructor(exact = SelectionBox<Any>("exact")).chosen == 2)
        check(NullableBoxSelectedConstructor(number = SelectionBox(1)).chosen == 1)
        check(NullableBoxSelectedConstructor(text = SelectionBox("text")).chosen == 2)
        check(NullableBoxSelectedConstructor(text = null).chosen == 2)
        check(NestedArraySelectedConstructor<String>(flat = arrayOf("a")).chosen == 1)
        check(NestedArraySelectedConstructor<String>(nested = arrayOf(arrayOf("b"))).chosen == 2)
        val tagged = TaggedSelectedConstructor<Int>(42, box = SelectionBox(1))
        val numbered = TaggedSelectedConstructor<String>(43, number = SelectionBox(2))
        check(tagged.chosen == 1 && tagged.tag == 42)
        check(numbered.chosen == 2 && numbered.tag == 43)
        val input = selectionInput()
        check(SelectionBoundHolder(input).write("imported") === input)
        check(input.read() == "imported")
        val star: SelectionInvariant<*> = input
        check(SelectionBox(star).value === input)
        val nullableChild = NullableSelectionOuter<Int>(null).Child("nullable")
        check(NullableSelectionHolder(nullableChild).child.value == "nullable")
        val genericHolder = GenericNullableSelectionHolder(109, nullableChild)
        check(genericHolder.marker == 109 && genericHolder.child.value == "nullable")
        check(CovariantSelectionOuter("imported").Child().read() == "imported")
        val genericChild = CovariantSelectionOuter("imported").GenericChild(107)
        check(genericChild.read() == "imported" && genericChild.own == 107)
        check(ReceiverSelectedConstructor { length }.run("receiver") == 8)
        val pair = System.Collections.Generic.KeyValuePair2<Int, String>(key = 79, value = "clr")
        check(pair.Key == 79 && pair.Value == "clr")
        val list = System.Collections.Generic.List<Int>(capacity = 2)
        check(list.Capacity >= 2)
        val factory = System.Threading.ThreadLocal<Int>({ 83 })
        check(factory.Value == 83)
        factory.Dispose()
        check(SelectedConstructor<Int>(value = 3).chosen == 1)
        check(SelectedConstructor<Int>(marker = 5).chosen == 2)
        check(SelectedConstructor<String>(value = "text").chosen == 1)
        check(SelectedConstructor<String>(marker = 7).chosen == 2)
        check(SelectedConstructor<Int?>(value = null).chosen == 1)
        check(NestedSelectedConstructor<Int>(box = SelectionBox(17)).chosen == 1)
        check(NestedSelectedConstructor<Int>(number = SelectionBox(19)).chosen == 2)
        check(MultiSelectedConstructor<Int, String>(first = 23, second = "a").chosen == 1)
        check(MultiSelectedConstructor<Int, String>(number = 31, other = "b").chosen == 2)
        check(NullableSelectedConstructor<Int>(value = null).chosen == 1)
        check(NullableSelectedConstructor<Int>(number = null).chosen == 2)
        check(ArraySelectedConstructor<String>(values = arrayOf("generic")).chosen == 1)
        check(ArraySelectedConstructor<String>(strings = arrayOf("concrete")).chosen == 2)
        val outer = SelectionOuter(67)
        check(outer.Child(value = 71).chosen == 1)
        val secondary = outer.Child(marker = 0)
        check(secondary.chosen == 2 && secondary.value == 67)
        checkImportedGenericSelection(73)
        checkImportedGenericSelection("method")
        checkImportedGenericSelection<Int?>(null)
    }
}
