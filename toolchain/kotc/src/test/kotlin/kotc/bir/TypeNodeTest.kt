package kotc.bir

import kotlin.test.Test
import kotlin.test.assertEquals
import kotc.backend.BirEmitter
import kotc.backend.constant
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.impl.IrFactoryImpl
import org.jetbrains.kotlin.ir.expressions.impl.IrConstImpl
import org.jetbrains.kotlin.ir.symbols.impl.IrTypeParameterSymbolImpl
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.defaultType
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.types.Variance

class TypeNodeTest {
    @OptIn(UnsafeDuringIrConstructionAPI::class)
    @Test
    fun actualIrConstantsConstructTypedSemanticValuesBeforeSerialization() {
        val parameter = IrFactoryImpl.createTypeParameter(0, 1, IrDeclarationOrigin.DEFINED,
            Name.identifier("T"), IrTypeParameterSymbolImpl(), Variance.INVARIANT, 0, false).apply {
            superTypes = emptyList()
        }
        val emitter = BirEmitter()
        val type = parameter.defaultType
        for (bits in listOf(0, Int.MIN_VALUE, 0x7f800000, 0xff800000.toInt(), 0x7fa12345, 0xffc12345.toInt(), 1)) {
            emitter.typeArgSubst[parameter] = TypeNode.Fqn("kotlin.Float")
            val constant = emitter.constant(IrConstImpl.float(0, 1, type, Float.fromBits(bits)))
            assertEquals(Float32Literal(bits), constant.value)
            println("IR_BIR_LITERAL_SAMPLE ${constant.toJson()}")
        }
        for (bits in listOf(0L, Long.MIN_VALUE, 0x7ff0000000000000L, 0xfff0000000000000uL.toLong(),
            0x7ff0123456789abcL, 0xfff8123456789abcuL.toLong(), 1L)) {
            emitter.typeArgSubst[parameter] = TypeNode.Fqn("kotlin.Double")
            val constant = emitter.constant(IrConstImpl.double(0, 1, type, Double.fromBits(bits)))
            assertEquals(Float64Literal(bits), constant.value)
            println("IR_BIR_LITERAL_SAMPLE ${constant.toJson()}")
        }
        val samples = listOf(null, true, false, '\ud800', Byte.MIN_VALUE, Short.MIN_VALUE,
            Int.MIN_VALUE, Long.MIN_VALUE, "a\ud800\u0000\udfff")
        for (sample in samples) {
            val literal = BirLiteral.fromValue(sample)
            println("IR_BIR_LITERAL_SAMPLE ${BirConstant(TypeNode.Fqn("kotlin.Any"), literal).toJson()}")
        }
    }

    @Test
    fun generatedSemanticVariantsKeepTheirJsonBoundaryShape() {
        val t = TypeNode.Fqn("T")
        val samples = listOf(
            t to """{"t":"fqn","name":"T"}""",
            TypeNode.Fqn("G", listOf(t)) to """{"t":"fqn","name":"G","args":[{"t":"fqn","name":"T"}]}""",
            TypeNode.Tv("method", 3) to """{"t":"tv","scope":"method","i":3}""",
            TypeNode.Star to """{"t":"star"}""",
            TypeNode.Projection("out", t) to """{"t":"projection","variance":"out","of":{"t":"fqn","name":"T"}}""",
            TypeNode.Fn(false, t, listOf(t)) to """{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"T"},"params":[{"t":"fqn","name":"T"}]}""",
            TypeNode.Nullable(t) to """{"t":"nullable","of":{"t":"fqn","name":"T"}}""",
            TypeNode.Oblivious(t) to """{"t":"oblivious","of":{"t":"fqn","name":"T"}}""",
            TypeNode.Array(t) to """{"t":"array","elem":{"t":"fqn","name":"T"}}""",
            TypeNode.ByRef(t) to """{"t":"byRef","of":{"t":"fqn","name":"T"}}""",
        )
        samples.forEach { (type, expected) ->
            assertEquals(expected, type.toJson())
            println("IR_TYPE_SAMPLE " + type.toJson())
        }
        val escaped = TypeNode.Fqn("Q\"\\\n\r\t\b\u000C\u0001κ😀")
        assertEquals(
            """{"t":"fqn","name":"Q\"\\\n\r\t\b\f\u0001κ😀"}""",
            escaped.toJson(),
        )
        println("IR_TYPE_SAMPLE " + escaped.toJson())
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
        println("IR_TYPE_SAMPLE " + type.toJson())
    }
}
