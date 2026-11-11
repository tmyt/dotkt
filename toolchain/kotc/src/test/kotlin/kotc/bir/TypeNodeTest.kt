package kotc.bir

import kotlin.test.Test
import kotlin.test.assertEquals

class TypeNodeTest {
    @Test
    fun generatedSemanticVariantsKeepTheirJsonBoundaryShape() {
        val t = TypeNode.Fqn("T")
        val samples = listOf(
            t to """{"t":"fqn","name":"T"}""",
            TypeNode.Tv("method", 3) to """{"t":"tv","scope":"method","i":3}""",
            TypeNode.Star to """{"t":"star"}""",
            TypeNode.Projection("out", t) to """{"t":"projection","variance":"out","of":{"t":"fqn","name":"T"}}""",
            TypeNode.Fn(false, t, listOf(t)) to """{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"T"},"params":[{"t":"fqn","name":"T"}]}""",
            TypeNode.Nullable(t) to """{"t":"nullable","of":{"t":"fqn","name":"T"}}""",
            TypeNode.Oblivious(t) to """{"t":"oblivious","of":{"t":"fqn","name":"T"}}""",
            TypeNode.Array(t) to """{"t":"array","elem":{"t":"fqn","name":"T"}}""",
            TypeNode.ByRef(t) to """{"t":"byRef","of":{"t":"fqn","name":"T"}}""",
        )
        samples.forEach { (type, expected) -> assertEquals(expected, type.toJson()) }
    }

    @Test
    fun classifierAndReceiverFactsSurviveNestedModelEncoding() {
        val source = TypeNode.SourceClassifier("demo", listOf("Outer"), "Inner")
        val receiver = TypeNode.Fqn("demo.Outer.Inner", classifier = source)
        val type = TypeNode.Fn(true, TypeNode.Tv("type", 1), emptyList(), recv = receiver)
        assertEquals(
            """{"t":"fn","suspend":true,"ret":{"t":"tv","scope":"type","i":1},"params":[],"recv":{"t":"fqn","name":"demo.Outer.Inner","classifier":{"packageName":"demo","ownerPath":["Outer"],"name":"Inner"}}}""",
            type.toJson(),
        )
    }
}
