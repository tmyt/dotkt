package roundtriptests.projectedclrconstruction

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.projectedclrconstruction.*

private class ProjectedStorageValue<T>(val value: T)

class ProjectedClrConstructionTests {
    @TestAttribute
    fun projectedNativeStorageRemainsWritableAcrossDifferentConstructions() {
        val integers = mutableListOf(7)
        val strings = mutableListOf("replacement")
        val nonNull = ProjectedConstructionInterop.NonNullBox<MutableList<*>>(integers)
        nonNull.Value = strings
        check(nonNull.Value === strings)
        val nullable = ProjectedConstructionInterop.ObliviousBox<MutableList<*>?>(null)
        nullable.Value = integers
        check(nullable.Value === integers)
        nullable.Value = null
        check(nullable.Value == null)

        val firstMarker: ProjectedConstructionInterop.MarkerValue<*> = ProjectedConstructionInterop.MarkerValue<Int>()
        val secondMarker: ProjectedConstructionInterop.MarkerValue<*> = ProjectedConstructionInterop.MarkerValue<String>()
        val constrained = ProjectedConstructionInterop.ConstrainedBox<ProjectedConstructionInterop.MarkerValue<*>>(firstMarker)
        constrained.Value = secondMarker
        check(constrained.Value === secondMarker)
        val dependent = ProjectedConstructionInterop.DependentBox<
            ProjectedConstructionInterop.MarkerValue<*>, ProjectedConstructionInterop.MarkerValue<*>>(firstMarker)
        dependent.Value = secondMarker
        check(dependent.Value === secondMarker)
        val reordered = ProjectedConstructionInterop.ReorderedDependentBox<
            ProjectedConstructionInterop.MarkerValue<*>, ProjectedConstructionInterop.MarkerValue<*>>(firstMarker)
        reordered.Value = secondMarker
        check(reordered.Value === secondMarker)

        val readonly = ProjectedConstructionInterop.ObliviousBox<List<*>>(listOf(7))
        val readonlyReplacement = listOf("readonly")
        readonly.Value = readonlyReplacement
        check(readonly.Value === readonlyReplacement)
        check(readonly.Value[0] == "readonly")
        val local = ProjectedConstructionInterop.ObliviousBox<ProjectedStorageValue<*>>(ProjectedStorageValue(7))
        val localReplacement = ProjectedStorageValue("local")
        local.Value = localReplacement
        check(local.Value === localReplacement)
        check(local.Value.value == "local")
    }

    @TestAttribute
    fun redundantProjectionConstructsTheClosedClrType() {
        val local = System.Collections.Generic.List<Comparable<in String>>()
        local.Add(TextOrder())
        assertEquals(5, consumeClosed(local))
        val imported = createProjected()
        assertEquals(5, consumeClosed(imported))
        val closed: System.Collections.Generic.List<Comparable<String>> = imported
        assertEquals(1, closed.Count)
        assertEquals(2, closed[0].compareTo("ok"))
        val covariant = createCovariant()
        assertEquals("covariant", consumeCovariant(covariant))
        val localCovariant = System.Collections.Generic.List<List<out String>>()
        localCovariant.Add(listOf("local"))
        assertEquals("local", consumeCovariant(localCovariant))
    }

    @TestAttribute
    fun genuineProjectionsKeepUsableConstructedStorage() {
        val starred = createStarred()
        assertEquals(2, starred.Count)
        assertEquals(7, starred[0])
        assertEquals("text", starred[1])
        val projected = createProjectedMutable()
        assertEquals(1, projected.Count)
        assertEquals("nested", projected[0][0])
        val integers = mutableListOf(7)
        val strings = mutableListOf("argument")
        check(nonNullProjectedArgument(integers) === integers)
        check(nonNullProjectedArgument(strings) === strings)
        check(obliviousProjectedArgument(integers) === integers)
        check(obliviousProjectedArgument(strings) === strings)
        val marker = ProjectedConstructionInterop.MarkerValue<Int>()
        check(constrainedProjectedArgument(marker) === marker)
    }
}
