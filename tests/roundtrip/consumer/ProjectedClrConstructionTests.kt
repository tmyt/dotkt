package roundtriptests.projectedclrconstruction

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.projectedclrconstruction.*

class ProjectedClrConstructionTests {
    fun <A : BoundFace<*>> interfaceBound(value: A): A = value

    private class Forwarder<X, A : BoundValue<X>> {
        fun forward(value: A): A = referenceBound(value)
    }

    private fun <A : BoundValue<String>> exactBound(value: A): A =
        ProjectedConstructionInterop.NonNullBox<A>(referenceBound(value)).Value

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
        val bound = BoundValue<String>()
        check(exactBound(bound) === bound)
        check(Forwarder<String, BoundValue<String>>().forward(bound) === bound)
        val row = System.Reflection.Assembly.GetExecutingAssembly()
            .GetType("roundtriptests.projectedclrconstruction.ProjectedClrConstructionTests", true)!!
            .GetMethod("interfaceBound")!!.GetGenericArguments()[0]
        check(row.GenericParameterAttributes == System.Reflection.GenericParameterAttributes.None)
        val hidden: BoundValue<*> = BoundValue<Int>()
        check(referenceBound(hidden) === hidden)
        check(obliviousProjectedArgument(integers) === integers)
        check(obliviousProjectedArgument(strings) === strings)
        val marker = ProjectedConstructionInterop.MarkerValue<Int>()
        check(constrainedProjectedArgument(marker) === marker)
    }
}
