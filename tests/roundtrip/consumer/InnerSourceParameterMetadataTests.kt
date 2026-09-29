import NUnit.Framework.TestAttribute
import roundtrip.innersourceparameters.*

private fun widenInner(value: Owner<String>.Covariant<String>): Owner<String>.Covariant<Any?> = value
private fun narrowInner(value: Owner<String>.Contravariant<Any?>): Owner<String>.Contravariant<String> = value
private fun widenLeaf(value: Owner<String>.Middle<Int>.Leaf<String>): Owner<String>.Middle<Int>.Leaf<Any?> = value

class InnerSourceParameterMetadataTests {
    @TestAttribute
    fun importedInnerVarianceUsesSourceParameterIndices() {
        val owner = Owner<String>()
        check(widenInner(owner.Covariant("inner")).item == "inner")
        var received: Any? = null
        val sink = owner.Contravariant<Any?> { received = it }
        narrowInner(sink).accept("sink")
        check(received == "sink")
        val middle = owner.Middle<Int>()
        check(widenLeaf(middle.Leaf("leaf")).item == "leaf")
    }

    @TestAttribute
    fun importedInnerBoundsRetainEnclosingCompanionMapping() {
        val owner = Owner<String>()
        val value: Owner<String>.Bounded<*> = owner.Bounded(Concrete())
        check(value.item.previous.value == null)
        check(owner.storage.value == null)
        val marked = owner.Marked(NullableMarker(), "unconstrained")
        check(marked.item.read() == 17 && marked.other == "unconstrained")
        val projected = owner.Projected(StringMarker(), 29)
        check(projected.item.read() == "projected" && projected.other == 29)
        val dependent = owner.Dependent<StringMarker, String>(StringMarker())
        check(dependent.read() == "projected")
    }
}
