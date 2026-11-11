package kotc.bir

import kotlin.test.Test
import kotlin.test.assertEquals
import kotc.backend.BirEmitter
import kotc.backend.typeParameterDeclarations
import kotc.backend.typeParamDeclarationsJson
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.impl.IrFactoryImpl
import org.jetbrains.kotlin.ir.symbols.impl.IrTypeParameterSymbolImpl
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.types.Variance

class TypeParameterDeclarationTest {
    @OptIn(UnsafeDuringIrConstructionAPI::class)
    @Test
    fun actualIrDeclarationProducerKeepsOneSemanticSlotPerParameter() {
        fun parameter(name: String, index: Int, variance: Variance, reified: Boolean) =
            IrFactoryImpl.createTypeParameter(0, 1, IrDeclarationOrigin.DEFINED,
                Name.identifier(name), IrTypeParameterSymbolImpl(), variance, index, reified).apply {
                superTypes = emptyList()
            }
        val parameters = listOf(parameter("T", 0, Variance.INVARIANT, false),
            parameter("R", 1, Variance.OUT_VARIANCE, true))
        val emitter = BirEmitter()
        assertEquals(listOf(TypeParameterDeclaration("T"),
            TypeParameterDeclaration("R", reified = true, variance = "out")),
            emitter.typeParameterDeclarations(parameters))
        val json = emitter.typeParamDeclarationsJson(parameters)
        assertEquals("""[{"name":"T"},{"name":"R","reified":true,"variance":"out"}]""", json)
        println("IR_TYPE_PARAMETER_SAMPLE $json")
    }

    @Test
    fun everySlotCarriesAnExplicitName() {
        assertEquals("""{"name":"T"}""", TypeParameterDeclaration("T").toJson())
        assertEquals("""{"name":"T\\\""}""", TypeParameterDeclaration("T\\\"").toJson())
    }

    @Test
    fun semanticBoundsKeepTheirPositionalFrameAndSourceFacts() {
        val declaration = TypeParameterDeclaration(
            name = "T", reified = true, variance = "out",
            constraints = listOf(TypeNode.Fqn("kotlin.Comparable", listOf(TypeNode.Tv("method", 0)))),
        )
        assertEquals(
            """{"name":"T","reified":true,"variance":"out","constraints":[{"t":"fqn","name":"kotlin.Comparable","args":[{"t":"tv","scope":"method","i":0}]}]}""",
            declaration.toJson(),
        )
        println("IR_TYPE_PARAMETER_SAMPLE [${declaration.toJson()}]")
    }
}
