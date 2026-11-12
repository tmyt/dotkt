package kotc.bir

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertSame
import kotc.backend.BirEmitter
import kotc.backend.constant
import kotc.backend.parameterNullCheck
import kotc.backend.nonNullResult
import kotc.backend.nullConstant
import kotc.backend.sourceValueBlock
import kotc.backend.yieldsNullAtJoin
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.impl.IrFactoryImpl
import org.jetbrains.kotlin.ir.expressions.impl.IrConstImpl
import org.jetbrains.kotlin.ir.symbols.impl.IrTypeParameterSymbolImpl
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.defaultType
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.types.Variance

class TypeNodeTest {
    @Test
    fun sourceCompositeNodesKeepTheirTypedChildrenAndClosedTypeFrame() {
        val frame = TypeNode.Tv("method", 1)
        val input = BirLocal("input", sty = TypeNode.Fqn("kotlin.Any"))
        val cast = BirCast(TypeNode.Array(frame), input, reifiedTypeOperand = false)
        val index = BirConstant(TypeNode.Fqn("kotlin.Int"), BirLiteral.IntegerLiteral(0))
        val element = BirArrayGet(cast, index)
        val spread = BirSpreadConcat(frame, listOf(
            BirSpreadPart(false, element), BirSpreadPart(true, cast),
        ))
        assertSame(input, cast.e)
        assertSame(cast, element.array)
        assertSame(element, spread.parts[0].e)
        assertSame(cast, spread.parts[1].e)
        assertEquals(frame, spread.elem)
        assertEquals(false, cast.reifiedTypeOperand)
        assertEquals(null, cast.copy(reifiedTypeOperand = null).reifiedTypeOperand)
        assertEquals(
            """{"k":"spreadConcat","elem":{"t":"tv","scope":"method","i":1},"parts":[{"spread":false,"e":{"k":"arrayGet","array":{"k":"cast","type":{"t":"array","elem":{"t":"tv","scope":"method","i":1}},"reifiedTypeOperand":false,"e":{"k":"local","name":"input","sty":{"t":"fqn","name":"kotlin.Any"}}},"index":{"k":"const","type":{"t":"fqn","name":"kotlin.Int"},"value":0}}},{"spread":true,"e":{"k":"cast","type":{"t":"array","elem":{"t":"tv","scope":"method","i":1}},"reifiedTypeOperand":false,"e":{"k":"local","name":"input","sty":{"t":"fqn","name":"kotlin.Any"}}}}]}""",
            spread.toJson(),
        )
        val samples = listOf(
            BirBinaryOperation("-", index, index),
            cast, BirIsInstance(frame, input), BirInstanceOrNull(frame, input, true),
            BirSafeValueCast(frame, input), BirConcat(listOf(input)), BirRuntimeClass(input),
            BirNullableWrap(frame, element), element, BirArraySet(cast, index, input), BirArrayLength(cast),
            BirNewArray(frame, listOf(element)), BirNewArraySized(frame, index),
            BirNewArrayInit(frame, index, input), spread, BirDefaultValue(frame),
            BirEnumName(frame, input), BirEnumOrdinal(input, frame), BirEnumParse(frame, input),
            BirObjectMethod("equals", input, input),
            BirDefaultArgument,
        )
        samples.forEach { assertEquals(false, it.yieldsNullAtJoin()) }
        val binding = BirCallBinding("selected", "default", "value", false, frame, "value", input)
        val call = BirCallEvaluation(frame, listOf(binding), BirBindingReference(binding.id, frame), "owner")
        assertSame(input, call.bindings.single().expr)
        assertEquals(false, call.yieldsNullAtJoin())
        assertEquals(
            """{"k":"callEval","type":{"t":"tv","scope":"method","i":1},"semanticOwner":"owner","bindings":[{"id":"selected","phase":"default","kind":"value","stable":false,"type":{"t":"tv","scope":"method","i":1},"role":"value","expr":{"k":"local","name":"input","sty":{"t":"fqn","name":"kotlin.Any"}}}],"expr":{"k":"bindRef","id":"selected","sty":{"t":"tv","scope":"method","i":1}}}""",
            call.toJson(),
        )
        assertEquals("""{"k":"defaultArg"}""", BirDefaultArgument.toJson())
    }

    @Test
    fun actualNonNullResultsKeepTypedBranchesAndExceptionMessages() {
        val emitter = BirEmitter()
        val value = BirLocal("bound")
        val failure = BirThrowExpression(emitter.newExc("kotlin.NullPointerException", null))
        val reference = nonNullResult(value, failure)
        assertEquals(BirUnaryOperation("!", BirObjectEquals(value, nullConstant())), reference.cond)
        assertEquals(value, reference.thenValue)
        assertEquals(failure, reference.elseValue)
        assertEquals(
            """{"k":"cond","cond":{"k":"unaryOp","op":"!","e":{"k":"objEq","lhs":{"k":"local","name":"bound"},"rhs":{"k":"const","type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Nothing"}},"value":null}}},"then":{"k":"local","name":"bound"},"else":{"k":"throwExpr","value":{"k":"new","type":{"t":"fqn","name":"kotlin.NullPointerException"},"argTypes":[],"args":[]}}}""",
            reference.toJson(),
        )
        val elem = TypeNode.Tv("method", 1)
        val nullable = nonNullResult(value, failure, elem)
        assertEquals(BirNullableHasValue(elem, value), nullable.cond)
        assertEquals(BirNullableValue(elem, value), nullable.thenValue)
        assertEquals(failure, nullable.elseValue)
        val text = "quote\"\\\n\ud800"
        val exception = emitter.newExc("kotlin.IllegalArgumentException", text)
        assertEquals(BirConstant(TypeNode.Fqn("kotlin.String"), BirLiteral.TextLiteral(text)), exception.args.single())
        assertEquals(listOf(TypeNode.Nullable(TypeNode.Fqn("kotlin.String"))), exception.argTypes)
    }

    @Test
    fun typedValueBlocksCarryOnlyRootJoinFacts() {
        val nullValue = nullConstant()
        val inner = sourceValueBlock(emptyList(), nullValue)
        val outer = sourceValueBlock(listOf(BirExpressionStatement(BirLocal("effect"))), inner)
        assertEquals(true, outer.joinNullBranch)
        assertEquals(true, outer.yieldsNullAtJoin())
        assertEquals(false, sourceValueBlock(listOf(BirExpressionStatement(inner)), BirLocal("value")).yieldsNullAtJoin())
        val transfer = sourceValueBlock(listOf(BirGoto(7)), BirThrowExpression(nullValue))
        assertEquals(false, transfer.yieldsNullAtJoin())
        assertEquals(
            """{"k":"valueBlock","stmts":[{"k":"goto","id":7}],"result":{"k":"throwExpr","value":{"k":"const","type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Nothing"}},"value":null}}}""",
            transfer.toJson(),
        )
    }

    @Test
    fun actualParameterGuardConstructsATypedGraph() {
        val guard = parameterNullCheck("input", "message")
        val branch = guard.branches.single() as BirConditionalBranch
        val condition = branch.cond as BirObjectEquals
        assertEquals(BirLocal("input"), condition.lhs)
        assertEquals(BirLiteral.NullLiteral, (condition.rhs as BirConstant).value)
        val exception = (branch.body.single() as BirThrow).value as BirNew
        assertEquals(TypeNode.Fqn("kotlin.NullPointerException"), exception.type)
        assertEquals(listOf(TypeNode.Nullable(TypeNode.Fqn("kotlin.String"))), exception.argTypes)
        assertEquals(BirLiteral.TextLiteral("message"), (exception.args.single() as BirConstant).value)
        assertEquals(
            """{"k":"if","branches":[{"cond":{"k":"objEq","lhs":{"k":"local","name":"input"},"rhs":{"k":"const","type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Nothing"}},"value":null}},"body":[{"k":"throw","value":{"k":"new","type":{"t":"fqn","name":"kotlin.NullPointerException"},"argTypes":[{"t":"nullable","of":{"t":"fqn","name":"kotlin.String"}}],"args":[{"k":"const","type":{"t":"fqn","name":"kotlin.String"},"value":"message"}]}}]}]}""",
            guard.toJson(),
        )
    }

    @Test
    fun typedSourceFieldsPreserveAbsentNullAndAnExpression() {
        val type = TypeNode.Fqn("kotlin.String")
        val absent = BirVariable("v", type)
        val uninitialized = absent.copy(init = BirField.Null)
        val initialized = absent.copy(init = BirField.Value(BirConstant(type, BirLiteral.TextLiteral("v"))))
        assertEquals("""{"k":"var","name":"v","type":{"t":"fqn","name":"kotlin.String"}}""", absent.toJson())
        assertEquals("""{"k":"var","name":"v","type":{"t":"fqn","name":"kotlin.String"},"init":null}""", uninitialized.toJson())
        assertEquals("""{"k":"var","name":"v","type":{"t":"fqn","name":"kotlin.String"},"init":{"k":"const","type":{"t":"fqn","name":"kotlin.String"},"value":"v"}}""", initialized.toJson())
        val branch = BirElseBranch(listOf(BirExpressionStatement(BirLocal("v"))))
        val output = StringBuilder()
        branch.writeJson(output)
        assertEquals("""{"else":true,"body":[{"k":"exprStmt","expr":{"k":"local","name":"v"}}]}""", output.toString())
    }

    @Test
    fun sourceCoreNodesKeepSemanticFieldsUntilTheBoundary() {
        val samples: List<Pair<BirNode, String>> = listOf(
            BirLocal("scope value", outer = false, sty = TypeNode.Tv("method", 1)) to
                """{"k":"local","name":"scope value","outer":false,"sty":{"t":"tv","scope":"method","i":1}}""",
            BirThis(TypeNode.Fqn("demo.Owner")) to
                """{"k":"this","sty":{"t":"fqn","name":"demo.Owner"}}""",
            BirSourcePoint() to """{"k":"sourcePoint"}""",
            BirSourcePoint(BirSourceSpan("src/κ.kt", 3, 4, 3, 7)) to
                """{"k":"sourcePoint","span":{"f":"src/κ.kt","l":3,"c":4,"el":3,"ec":7}}""",
            BirBreak(null) to """{"k":"break","label":null}""",
            BirContinue("outer") to """{"k":"continue","label":"outer"}""",
            BirGoto(91) to """{"k":"goto","id":91}""",
            BirLabel(91) to """{"k":"label","id":91}""",
            BirEnumValue(TypeNode.Fqn("demo.Mode"), "Selected", 17) to
                """{"k":"enumValue","type":{"t":"fqn","name":"demo.Mode"},"entry":"Selected","ordinal":17}""",
            BirEnumValues(TypeNode.Tv("method", 1)) to
                """{"k":"enumValues","type":{"t":"tv","scope":"method","i":1}}""",
            BirCompanionValue(TypeNode.Fqn("demo.Owner"), TypeNode.Fqn("demo.Owner.Companion"), "Companion") to
                """{"k":"companionValue","ownerType":{"t":"fqn","name":"demo.Owner"},"companionType":{"t":"fqn","name":"demo.Owner.Companion"},"name":"Companion"}""",
            BirClassReference(TypeNode.Array(TypeNode.Tv("type", 0))) to
                """{"k":"classRef","type":{"t":"array","elem":{"t":"tv","scope":"type","i":0}}}""",
            BirNullableNull(TypeNode.Tv("method", 2)) to
                """{"k":"nullableNull","elem":{"t":"tv","scope":"method","i":2}}""",
            BirDefaultParameter(0) to """{"k":"defaultArgParam","idx":0}""",
            BirDefaultReceiver("dispatch") to """{"k":"defaultArgReceiver","kind":"dispatch"}""",
            BirDefaultReceiver("extension") to """{"k":"defaultArgReceiver","kind":"extension"}""",
            BirDefaultReceiver("enclosing") to """{"k":"defaultArgReceiver","kind":"enclosing"}""",
            BirBindingReference("bound\"value", TypeNode.ByRef(TypeNode.Tv("method", 3))) to
                """{"k":"bindRef","id":"bound\"value","sty":{"t":"byRef","of":{"t":"tv","scope":"method","i":3}}}""",
            BirLocalFunctionReference("selected", TypeNode.Fn(true, TypeNode.Tv("method", 1), emptyList()),
                listOf(TypeNode.Tv("type", 0))) to
                """{"k":"localFunRef","id":"selected","funcType":{"t":"fn","suspend":true,"ret":{"t":"tv","scope":"method","i":1},"params":[]},"typeArgs":[{"t":"tv","scope":"type","i":0}]}""",
            BirLocalFunctionReference("plain", TypeNode.Fn(false, TypeNode.Fqn("kotlin.Unit"), emptyList())) to
                """{"k":"localFunRef","id":"plain","funcType":{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"kotlin.Unit"},"params":[]}}""",
            BirLocalFunctionReference("empty", TypeNode.Fn(false, TypeNode.Fqn("kotlin.Unit"), emptyList()), emptyList()) to
                """{"k":"localFunRef","id":"empty","funcType":{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"kotlin.Unit"},"params":[]},"typeArgs":[]}""",
            BirNew(
                type = TypeNode.Fqn("demo.Owner", listOf(TypeNode.Tv("method", 1))),
                args = emptyList(), argTypes = emptyList(),
                constructorDeclarationId = "selected-declaration",
                memberVisibility = "protected",
                memberOwnerTypeParams = listOf(TypeParameterDeclaration("T", reified = false,
                    variance = "out", constraints = listOf(TypeNode.Fqn("kotlin.Any")))),
                memberSignature = listOf(TypeNode.Tv("type", 0)),
            ) to """{"k":"new","type":{"t":"fqn","name":"demo.Owner","args":[{"t":"tv","scope":"method","i":1}]},"argTypes":[],"args":[],"constructorDeclarationId":"selected-declaration","memberVisibility":"protected","memberOwnerTypeParams":[{"name":"T","reified":false,"variance":"out","constraints":[{"t":"fqn","name":"kotlin.Any"}]}],"memberSignature":[{"t":"tv","scope":"type","i":0}]}""",
        )
        samples.forEach { (node, expected) ->
            assertEquals(expected, node.toJson())
            if (node is BirExpression) assertEquals(false, node.yieldsNullAtJoin())
        }
        val output = StringBuilder("prefix:")
        BirConstant(TypeNode.Fqn("kotlin.Float"), Float32Literal(Int.MIN_VALUE)).writeJson(output)
        assertEquals("""prefix:{"k":"const","type":{"t":"fqn","name":"kotlin.Float"},"value":{"encoding":"f32","bits":-2147483648}}""", output.toString())
    }

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
        val samples = listOf(
            TypeNode.Nullable(TypeNode.Fqn("kotlin.Any")) to IrConstImpl.constNull(0, 1, type),
            TypeNode.Fqn("kotlin.Boolean") to IrConstImpl.boolean(0, 1, type, true),
            TypeNode.Fqn("kotlin.Boolean") to IrConstImpl.boolean(0, 1, type, false),
            TypeNode.Fqn("kotlin.Char") to IrConstImpl.char(0, 1, type, '\ud800'),
            TypeNode.Fqn("kotlin.Byte") to IrConstImpl.byte(0, 1, type, Byte.MIN_VALUE),
            TypeNode.Fqn("kotlin.Short") to IrConstImpl.short(0, 1, type, Short.MIN_VALUE),
            TypeNode.Fqn("kotlin.Int") to IrConstImpl.int(0, 1, type, Int.MIN_VALUE),
            TypeNode.Fqn("kotlin.Long") to IrConstImpl.long(0, 1, type, Long.MIN_VALUE),
            TypeNode.Fqn("kotlin.String") to IrConstImpl.string(0, 1, type, "a\ud800\u0000\udfff"),
        )
        for ((sourceType, sample) in samples) {
            emitter.typeArgSubst[parameter] = sourceType
            val constant = emitter.constant(sample)
            assertEquals(sourceType, constant.type)
            println("IR_BIR_LITERAL_SAMPLE ${constant.toJson()}")
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
