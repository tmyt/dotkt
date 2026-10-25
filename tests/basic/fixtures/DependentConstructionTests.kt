package dependentconstruction

import NUnit.Framework.TestAttribute

private interface Marker<Tag>
private open class Base<T>
private class StringItem : Base<StringItem>(), Marker<String>
private class IntItem : Base<IntItem>(), Marker<Int>
private class NullableItem : Base<NullableItem>(), Marker<Int?>
private class Owner<Tag, T> where T : Base<T>, T : Marker<Tag> {
    fun consume(value: T): T = value
    fun label(): String = "constructed"
}

private fun <Tag, T> constructWithoutValueSlot(): String
    where T : Base<T>, T : Marker<Tag> = Owner<Tag, T>().label()

private interface OutMarker<out Tag>
private class OutIntItem : OutMarker<Int>
private class VariantOwner<Tag, T> where T : OutMarker<Tag> {
    fun label(): String = "variant"
}
private fun <Tag, T> constructVariant(): String where T : OutMarker<Tag> = VariantOwner<Tag, T>().label()

private interface InMarker<in Tag>
private class InAnyItem : InMarker<Any>
private class ContravariantOwner<Tag, T> where T : InMarker<Tag> {
    fun label(): String = "contravariant"
}
private fun <Tag, T> constructContravariant(): String where T : InMarker<Tag> =
    ContravariantOwner<Tag, T>().label()

private interface ValueMarker<Tag> { fun value(): Tag }
private class ValueItem<Tag>(private val stored: Tag) : ValueMarker<Tag> {
    override fun value(): Tag = stored
}
private fun <Tag, T> identity(value: T): T where T : ValueMarker<Tag> = value
private class ErasedOwner<Tag> {
    fun <T> matches(value: T, expected: Any): Boolean where T : ValueMarker<Tag> = value === expected
}

class DependentConstructionTests {
    @TestAttribute fun independentInvariantBoundsPermitOrdinaryConstruction() {
        check(constructWithoutValueSlot<String, StringItem>() == "constructed")
        check(constructWithoutValueSlot<Int, IntItem>() == "constructed")
        check(constructWithoutValueSlot<Int?, NullableItem>() == "constructed")
        check(constructVariant<Any, OutIntItem>() == "variant")
        check(constructContravariant<Int, InAnyItem>() == "contravariant")
    }

    @TestAttribute fun erasedValuesAndOwnerViewsKeepTheirExistingContracts() {
        val original = ValueItem(37)
        @Suppress("UNCHECKED_CAST")
        val viewed = original as ValueMarker<String>
        check(identity<String, ValueMarker<String>>(viewed) === original)
        @Suppress("UNCHECKED_CAST")
        val owner = ErasedOwner<Int>() as ErasedOwner<String>
        check(owner.matches<ValueMarker<String>>(viewed, original))
    }
}
