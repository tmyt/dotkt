import NestedArityInterop.Outer
import NestedArityInterop.Oracle
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class NestedArityTests {
    @TestAttribute
    fun nestedClassifiersThatDifferOnlyByArityRemainDistinct() {
        assertEquals(1, Outer.Item().Value)
        assertEquals("generic", Outer.Item1("generic").Value)

        // A reference-metadata TypeNode uses dotted nesting (`Outer.ValueItem`), while reflection identifies the
        // declaration as `Outer+ValueItem`1`. The nullable wrapper must survive because the dotted classifier is a
        // struct, independently of which producer minted its token.
        assertEquals(true, Oracle.HasNestedValue(Oracle.NestedValue()))

        // A bare external struct construction entering a nullable local must be wrapped as Nullable<T>; the `!!`
        // present branch must then unwrap that same value before calling the struct member.
        val localValue: Outer.ValueItem<String>? = Outer.ValueItem("local nested value")
        assertEquals("local nested value", localValue!!.Value)

        // CLR permits class Kind and struct Kind<T> in one scope. dll2klib projects the latter as Kind1; the arity is
        // part of the oracle identity, so the class remains an NRT reference and the constructed struct remains a
        // structural Nullable<T> rather than whichever declaration happened to be scanned last.
        assertEquals(true, Oracle.HasReferenceKind(Oracle.ReferenceKind()))
        assertEquals(true, Oracle.HasValueKind(Oracle.ValueKind()))

        // Nested CLR declarations flatten the outer and inner generic arguments into one identity. The producer and
        // the BIR consumer must therefore agree that GenericOuter<Int>.Leaf<String> has arity two.
        assertEquals(true, Oracle.FlattenedNestedValue() != null)
        assertEquals(true, Oracle.HasFlattenedNestedValue(Oracle.FlattenedNestedValue()))
    }
}
