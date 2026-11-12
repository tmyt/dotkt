// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

import org.msgpack.core.MessagePacker

internal object BirMessagePack {
    private const val maxDepth = 1024
    private fun checkDepth(depth: Int) { require(depth < maxDepth) }

    fun write(value: BirValue, packer: MessagePacker, depth: Int = 0) {
        checkDepth(depth)
        when (value) {
            is TypeNode -> writeType(value, packer, depth)
            is BirLiteral -> writeLiteral(value, packer, depth)
            is SourceTypeAlias -> SourceMetadataMessagePack.write(value, packer)
            is TypeParameterDeclaration -> writeTypeParameterDeclaration(value, packer, depth)
            is BirFileDeclaration -> writeBirFileDeclaration(value, packer, depth)
            is BirBinaryOperation -> writeBirBinaryOperation(value, packer, depth)
            is BirCast -> writeBirCast(value, packer, depth)
            is BirIsInstance -> writeBirIsInstance(value, packer, depth)
            is BirInstanceOrNull -> writeBirInstanceOrNull(value, packer, depth)
            is BirSafeValueCast -> writeBirSafeValueCast(value, packer, depth)
            is BirConcat -> writeBirConcat(value, packer, depth)
            is BirRuntimeClass -> writeBirRuntimeClass(value, packer, depth)
            is BirNullableWrap -> writeBirNullableWrap(value, packer, depth)
            is BirArrayGet -> writeBirArrayGet(value, packer, depth)
            is BirArraySet -> writeBirArraySet(value, packer, depth)
            is BirArrayLength -> writeBirArrayLength(value, packer, depth)
            is BirNewArray -> writeBirNewArray(value, packer, depth)
            is BirNewArraySized -> writeBirNewArraySized(value, packer, depth)
            is BirNewArrayInit -> writeBirNewArrayInit(value, packer, depth)
            is BirSpreadPart -> writeBirSpreadPart(value, packer, depth)
            is BirSpreadConcat -> writeBirSpreadConcat(value, packer, depth)
            is BirDefaultValue -> writeBirDefaultValue(value, packer, depth)
            is BirEnumName -> writeBirEnumName(value, packer, depth)
            is BirEnumOrdinal -> writeBirEnumOrdinal(value, packer, depth)
            is BirEnumParse -> writeBirEnumParse(value, packer, depth)
            is BirObjectMethod -> writeBirObjectMethod(value, packer, depth)
            is BirCallBinding -> writeBirCallBinding(value, packer, depth)
            is BirCallEvaluation -> writeBirCallEvaluation(value, packer, depth)
            is BirDefaultArgument -> writeBirDefaultArgument(value, packer, depth)
            is BirOverride -> writeBirOverride(value, packer, depth)
            is BirCallStatic -> writeBirCallStatic(value, packer, depth)
            is BirCallInstance -> writeBirCallInstance(value, packer, depth)
            is BirCallLocal -> writeBirCallLocal(value, packer, depth)
            is BirFieldGet -> writeBirFieldGet(value, packer, depth)
            is BirStaticFieldGet -> writeBirStaticFieldGet(value, packer, depth)
            is BirFieldSetExpression -> writeBirFieldSetExpression(value, packer, depth)
            is BirStaticFieldSet -> writeBirStaticFieldSet(value, packer, depth)
            is BirLateinitGet -> writeBirLateinitGet(value, packer, depth)
            is BirLateinitIsInitialized -> writeBirLateinitIsInitialized(value, packer, depth)
            is BirModifiers -> writeBirModifiers(value, packer, depth)
            is BirDefaultBody -> writeBirDefaultBody(value, packer, depth)
            is BirAttributeNamedArgument -> writeBirAttributeNamedArgument(value, packer, depth)
            is BirAttribute -> writeBirAttribute(value, packer, depth)
            is BirParameter -> writeBirParameter(value, packer, depth)
            is BirDefaultCarrier -> writeBirDefaultCarrier(value, packer, depth)
            is BirLocalFunction -> writeBirLocalFunction(value, packer, depth)
            is BirInheritedImplementation -> writeBirInheritedImplementation(value, packer, depth)
            is BirDeclarationPosition -> writeBirDeclarationPosition(value, packer, depth)
            is BirFunctionDeclaration -> writeBirFunctionDeclaration(value, packer, depth)
            is BirConstructor -> writeBirConstructor(value, packer, depth)
            is BirFieldDeclaration -> writeBirFieldDeclaration(value, packer, depth)
            is BirRefTypeDeclaration -> writeBirRefTypeDeclaration(value, packer, depth)
            is BirPropertyDeclaration -> writeBirPropertyDeclaration(value, packer, depth)
            is BirInterfaceDefinition -> writeBirInterfaceDefinition(value, packer, depth)
            is BirEnumEntryDeclaration -> writeBirEnumEntryDeclaration(value, packer, depth)
            is BirClrEnumDeclaration -> writeBirClrEnumDeclaration(value, packer, depth)
            is BirEnumDefinition -> writeBirEnumDefinition(value, packer, depth)
            is BirClassDefinition -> writeBirClassDefinition(value, packer, depth)
            is BirKotlinCompanion -> writeBirKotlinCompanion(value, packer, depth)
            is BirRichEnumEntry -> writeBirRichEnumEntry(value, packer, depth)
            is BirRichEnumDeclaration -> writeBirRichEnumDeclaration(value, packer, depth)
            is BirInheritedDefaultAccessor -> writeBirInheritedDefaultAccessor(value, packer, depth)
            is BirInheritedDefaultMethod -> writeBirInheritedDefaultMethod(value, packer, depth)
            is BirInheritedClassMethod -> writeBirInheritedClassMethod(value, packer, depth)
            is BirClrEventBacking -> writeBirClrEventBacking(value, packer, depth)
            is BirClrEventForwarder -> writeBirClrEventForwarder(value, packer, depth)
            is BirClrEventAccessor -> writeBirClrEventAccessor(value, packer, depth)
            is BirNewSam -> writeBirNewSam(value, packer, depth)
            is BirSamConversion -> writeBirSamConversion(value, packer, depth)
            is BirCaptureDescriptor -> writeBirCaptureDescriptor(value, packer, depth)
            is BirClosureDefinition -> writeBirClosureDefinition(value, packer, depth)
            is BirNewClosure -> writeBirNewClosure(value, packer, depth)
            is BirNewDelegate -> writeBirNewDelegate(value, packer, depth)
            is BirNewSuspendLambda -> writeBirNewSuspendLambda(value, packer, depth)
            is BirEventGet -> writeBirEventGet(value, packer, depth)
            is BirEventRaise -> writeBirEventRaise(value, packer, depth)
            is BirLambdaParameter -> writeBirLambdaParameter(value, packer, depth)
            is BirInlineCapture -> writeBirInlineCapture(value, packer, depth)
            is BirInlineLambda -> writeBirInlineLambda(value, packer, depth)
            is BirStackAllocation -> writeBirStackAllocation(value, packer, depth)
            is BirStackGet -> writeBirStackGet(value, packer, depth)
            is BirStackSet -> writeBirStackSet(value, packer, depth)
            is BirStackAsSpan -> writeBirStackAsSpan(value, packer, depth)
            is BirInlineReceivers -> writeBirInlineReceivers(value, packer, depth)
            is BirCallInline -> writeBirCallInline(value, packer, depth)
            is BirNewBoundDelegate -> writeBirNewBoundDelegate(value, packer, depth)
            is BirByRefOf -> writeBirByRefOf(value, packer, depth)
            is BirByRefLoad -> writeBirByRefLoad(value, packer, depth)
            is BirByRefStore -> writeBirByRefStore(value, packer, depth)
            is BirReturnExpression -> writeBirReturnExpression(value, packer, depth)
            is BirUnsupportedExpression -> writeBirUnsupportedExpression(value, packer, depth)
            is BirPropertyFunction -> writeBirPropertyFunction(value, packer, depth)
            is BirConstant -> writeBirConstant(value, packer, depth)
            is BirObjectEquals -> writeBirObjectEquals(value, packer, depth)
            is BirExpressionStatement -> writeBirExpressionStatement(value, packer, depth)
            is BirThrowExpression -> writeBirThrowExpression(value, packer, depth)
            is BirUnaryOperation -> writeBirUnaryOperation(value, packer, depth)
            is BirConditional -> writeBirConditional(value, packer, depth)
            is BirNullableHasValue -> writeBirNullableHasValue(value, packer, depth)
            is BirNullableValue -> writeBirNullableValue(value, packer, depth)
            is BirEnumValue -> writeBirEnumValue(value, packer, depth)
            is BirEnumValues -> writeBirEnumValues(value, packer, depth)
            is BirCompanionValue -> writeBirCompanionValue(value, packer, depth)
            is BirClassReference -> writeBirClassReference(value, packer, depth)
            is BirNullableNull -> writeBirNullableNull(value, packer, depth)
            is BirDefaultParameter -> writeBirDefaultParameter(value, packer, depth)
            is BirDefaultReceiver -> writeBirDefaultReceiver(value, packer, depth)
            is BirBindingReference -> writeBirBindingReference(value, packer, depth)
            is BirLocalFunctionReference -> writeBirLocalFunctionReference(value, packer, depth)
            is BirValueBlock -> writeBirValueBlock(value, packer, depth)
            is BirNew -> writeBirNew(value, packer, depth)
            is BirThrow -> writeBirThrow(value, packer, depth)
            is BirIf -> writeBirIf(value, packer, depth)
            is BirReturn -> writeBirReturn(value, packer, depth)
            is BirSetLocal -> writeBirSetLocal(value, packer, depth)
            is BirSetField -> writeBirSetField(value, packer, depth)
            is BirBlock -> writeBirBlock(value, packer, depth)
            is BirBranchIf -> writeBirBranchIf(value, packer, depth)
            is BirCatch -> writeBirCatch(value, packer, depth)
            is BirTry -> writeBirTry(value, packer, depth)
            is BirForArray -> writeBirForArray(value, packer, depth)
            is BirForIn -> writeBirForIn(value, packer, depth)
            is BirConditionalBranch -> writeBirConditionalBranch(value, packer, depth)
            is BirElseBranch -> writeBirElseBranch(value, packer, depth)
            is BirVariable -> writeBirVariable(value, packer, depth)
            is BirLocal -> writeBirLocal(value, packer, depth)
            is BirThis -> writeBirThis(value, packer, depth)
            is BirSourcePoint -> writeBirSourcePoint(value, packer, depth)
            is BirBreak -> writeBirBreak(value, packer, depth)
            is BirContinue -> writeBirContinue(value, packer, depth)
            is BirGoto -> writeBirGoto(value, packer, depth)
            is BirLabel -> writeBirLabel(value, packer, depth)
            is BirSourceSpan -> writeBirSourceSpan(value, packer, depth)
        }
    }

    private fun writeBirFileDeclaration(value: BirFileDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(8)
        packer.packString("fileClass")
        MessagePackText.write(packer, value.fileClass)
        packer.packString("hasMain")
        packer.packBoolean(value.hasMain)
        packer.packString("fields")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.fields.size)
        for (item0 in value.fields) {
            writeBirFieldDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("methods")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.methods.size)
        for (item0 in value.methods) {
            writeBirFunctionDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("properties")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.properties.size)
        for (item0 in value.properties) {
            writeBirPropertyDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("types")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.types.size)
        for (item0 in value.types) {
            write(item0, packer, depth + 1 + 1)
        }
        packer.packString("refTypes")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.refTypes.size)
        for (item0 in value.refTypes) {
            writeBirRefTypeDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("typeAliases")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.typeAliases.size)
        for (item0 in value.typeAliases) {
            SourceMetadataMessagePack.write(item0, packer)
        }
    }

    private fun writeBirBinaryOperation(value: BirBinaryOperation, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("binOp")
        packer.packString("op")
        MessagePackText.write(packer, value.op)
        packer.packString("lhs")
        write(value.lhs, packer, depth + 1)
        packer.packString("rhs")
        write(value.rhs, packer, depth + 1)
    }

    private fun writeBirCast(value: BirCast, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.reifiedTypeOperand == null) 0 else 1))
        packer.packString("k")
        packer.packString("cast")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.reifiedTypeOperand?.let { child ->
            packer.packString("reifiedTypeOperand")
            packer.packBoolean(child)
        }
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirIsInstance(value: BirIsInstance, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("isInst")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirInstanceOrNull(value: BirInstanceOrNull, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.reifiedTypeOperand == null) 0 else 1))
        packer.packString("k")
        packer.packString("isInstRef")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.reifiedTypeOperand?.let { child ->
            packer.packString("reifiedTypeOperand")
            packer.packBoolean(child)
        }
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirSafeValueCast(value: BirSafeValueCast, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("safeCastValue")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirConcat(value: BirConcat, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("concat")
        packer.packString("parts")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.parts.size)
        for (item0 in value.parts) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirRuntimeClass(value: BirRuntimeClass, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("getType")
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirNullableWrap(value: BirNullableWrap, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("nullableWrap")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirArrayGet(value: BirArrayGet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("arrayGet")
        packer.packString("array")
        write(value.array, packer, depth + 1)
        packer.packString("index")
        write(value.index, packer, depth + 1)
    }

    private fun writeBirArraySet(value: BirArraySet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("arraySet")
        packer.packString("array")
        write(value.array, packer, depth + 1)
        packer.packString("index")
        write(value.index, packer, depth + 1)
        packer.packString("value")
        write(value.value, packer, depth + 1)
    }

    private fun writeBirArrayLength(value: BirArrayLength, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("arrayLen")
        packer.packString("array")
        write(value.array, packer, depth + 1)
    }

    private fun writeBirNewArray(value: BirNewArray, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("newArray")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("elems")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.elems.size)
        for (item0 in value.elems) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirNewArraySized(value: BirNewArraySized, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("newArraySized")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("size")
        write(value.size, packer, depth + 1)
    }

    private fun writeBirNewArrayInit(value: BirNewArrayInit, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("newArrayInit")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("size")
        write(value.size, packer, depth + 1)
        packer.packString("init")
        write(value.init, packer, depth + 1)
    }

    private fun writeBirSpreadPart(value: BirSpreadPart, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("spread")
        packer.packBoolean(value.spread)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirSpreadConcat(value: BirSpreadConcat, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("spreadConcat")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("parts")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.parts.size)
        for (item0 in value.parts) {
            writeBirSpreadPart(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirDefaultValue(value: BirDefaultValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("default")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
    }

    private fun writeBirEnumName(value: BirEnumName, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("enumName")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirEnumOrdinal(value: BirEnumOrdinal, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("enumOrdinal")
        packer.packString("e")
        write(value.e, packer, depth + 1)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
    }

    private fun writeBirEnumParse(value: BirEnumParse, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("enumParse")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("arg")
        write(value.arg, packer, depth + 1)
    }

    private fun writeBirObjectMethod(value: BirObjectMethod, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.arg == null) 0 else 1))
        packer.packString("k")
        packer.packString("objMethod")
        packer.packString("method")
        MessagePackText.write(packer, value.method)
        packer.packString("recv")
        write(value.recv, packer, depth + 1)
        value.arg?.let { child ->
            packer.packString("arg")
            write(child, packer, depth + 1)
        }
    }

    private fun writeBirCallBinding(value: BirCallBinding, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(7)
        packer.packString("id")
        MessagePackText.write(packer, value.id)
        packer.packString("phase")
        MessagePackText.write(packer, value.phase)
        packer.packString("kind")
        MessagePackText.write(packer, value.kind)
        packer.packString("stable")
        packer.packBoolean(value.stable)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("role")
        MessagePackText.write(packer, value.role)
        packer.packString("expr")
        write(value.expr, packer, depth + 1)
    }

    private fun writeBirCallEvaluation(value: BirCallEvaluation, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.semanticOwner == null) 0 else 1))
        packer.packString("k")
        packer.packString("callEval")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.semanticOwner?.let { child ->
            packer.packString("semanticOwner")
            MessagePackText.write(packer, child)
        }
        packer.packString("bindings")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.bindings.size)
        for (item0 in value.bindings) {
            writeBirCallBinding(item0, packer, depth + 1 + 1)
        }
        packer.packString("expr")
        write(value.expr, packer, depth + 1)
    }

    private fun writeBirDefaultArgument(value: BirDefaultArgument, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1)
        packer.packString("k")
        packer.packString("defaultArg")
    }

    private fun writeBirOverride(value: BirOverride, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.declarationId == null) 0 else 1))
        packer.packString("owner")
        writeType(value.owner, packer, depth + 1)
        packer.packString("member")
        MessagePackText.write(packer, value.member)
        packer.packString("kind")
        MessagePackText.write(packer, value.kind)
        packer.packString("arity")
        packer.packInt(value.arity)
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirCallStatic(value: BirCallStatic, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.companionCall == null) 0 else 1) + (if (value.owner == BirField.Absent) 0 else 1) + (if (value.ownerType == null) 0 else 1) + (if (value.prop == null) 0 else 1) + (if (value.sig == null) 0 else 1) + (if (value.typeArgs == null) 0 else 1) + (if (value.shapeTypes == null) 0 else 1) + (if (value.argTypes == null) 0 else 1) + (if (value.ret == null) 0 else 1) + (if (value.suspendCall == null) 0 else 1) + (if (value.clrAwaitBridge == null) 0 else 1) + (if (value.awaitResult == null) 0 else 1) + (if (value.calleeOwner == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.declarationId == null) 0 else 1) + (if (value.defaultDeclarationId == null) 0 else 1) + (if (value.defaultOwnerType == null) 0 else 1) + (if (value.dotktFrontendDeclarationConsumed == null) 0 else 1) + (if (value.clrFlagsOperation == null) 0 else 1) + (if (value.anySlot == null) 0 else 1) + (if (value.sty == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberMethodTypeParams == null) 0 else 1) + (if (value.memberSignature == null) 0 else 1) + (if (value.memberReturnType == null) 0 else 1))
        packer.packString("k")
        packer.packString("callStatic")
        value.companionCall?.let { child ->
            packer.packString("companionCall")
            packer.packBoolean(child)
        }
        when (val present = value.owner) {
            BirField.Absent -> {}
            BirField.Null -> {
                packer.packString("owner")
                packer.packNil()
            }
            is BirField.Value -> {
                packer.packString("owner")
                writeType(present.value, packer, depth + 1)
            }
        }
        value.ownerType?.let { child ->
            packer.packString("ownerType")
            writeType(child, packer, depth + 1)
        }
        packer.packString("method")
        MessagePackText.write(packer, value.method)
        value.prop?.let { child ->
            packer.packString("prop")
            MessagePackText.write(packer, child)
        }
        value.sig?.let { child ->
            packer.packString("sig")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.shapeTypes?.let { child ->
            packer.packString("shapeTypes")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.argTypes?.let { child ->
            packer.packString("argTypes")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.ret?.let { child ->
            packer.packString("ret")
            writeType(child, packer, depth + 1)
        }
        packer.packString("args")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.args.size)
        for (item0 in value.args) {
            write(item0, packer, depth + 1 + 1)
        }
        value.suspendCall?.let { child ->
            packer.packString("suspendCall")
            packer.packBoolean(child)
        }
        value.clrAwaitBridge?.let { child ->
            packer.packString("clrAwaitBridge")
            packer.packBoolean(child)
        }
        value.awaitResult?.let { child ->
            packer.packString("awaitResult")
            writeType(child, packer, depth + 1)
        }
        value.calleeOwner?.let { child ->
            packer.packString("calleeOwner")
            writeType(child, packer, depth + 1)
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
        value.defaultDeclarationId?.let { child ->
            packer.packString("defaultDeclarationId")
            MessagePackText.write(packer, child)
        }
        value.defaultOwnerType?.let { child ->
            packer.packString("defaultOwnerType")
            writeType(child, packer, depth + 1)
        }
        value.dotktFrontendDeclarationConsumed?.let { child ->
            packer.packString("dotktFrontendDeclarationConsumed")
            packer.packBoolean(child)
        }
        value.clrFlagsOperation?.let { child ->
            packer.packString("clrFlagsOperation")
            MessagePackText.write(packer, child)
        }
        value.anySlot?.let { child ->
            packer.packString("anySlot")
            packer.packBoolean(child)
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberMethodTypeParams?.let { child ->
            packer.packString("memberMethodTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberSignature?.let { child ->
            packer.packString("memberSignature")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.memberReturnType?.let { child ->
            packer.packString("memberReturnType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirCallInstance(value: BirCallInstance, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.companionCall == null) 0 else 1) + (if (value.ownerType == null) 0 else 1) + (if (value.virtual == null) 0 else 1) + (if (value.prop == null) 0 else 1) + (if (value.sig == null) 0 else 1) + (if (value.typeArgs == null) 0 else 1) + (if (value.shapeTypes == null) 0 else 1) + (if (value.argTypes == null) 0 else 1) + (if (value.dynRet == null) 0 else 1) + (if (value.ret == null) 0 else 1) + (if (value.suspendCall == null) 0 else 1) + (if (value.clrAwaitBridge == null) 0 else 1) + (if (value.awaitResult == null) 0 else 1) + (if (value.overrides == null) 0 else 1) + (if (value.anySlot == null) 0 else 1) + (if (value.superCall == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.declarationId == null) 0 else 1) + (if (value.defaultDeclarationId == null) 0 else 1) + (if (value.defaultOwnerType == null) 0 else 1) + (if (value.dotktFrontendDeclarationConsumed == null) 0 else 1) + (if (value.clrFlagsOperation == null) 0 else 1) + (if (value.sty == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberMethodTypeParams == null) 0 else 1) + (if (value.memberSignature == null) 0 else 1) + (if (value.memberReturnType == null) 0 else 1))
        packer.packString("k")
        packer.packString("callInstance")
        value.companionCall?.let { child ->
            packer.packString("companionCall")
            packer.packBoolean(child)
        }
        value.ownerType?.let { child ->
            packer.packString("ownerType")
            writeType(child, packer, depth + 1)
        }
        value.virtual?.let { child ->
            packer.packString("virtual")
            packer.packBoolean(child)
        }
        packer.packString("recv")
        write(value.recv, packer, depth + 1)
        packer.packString("method")
        MessagePackText.write(packer, value.method)
        value.prop?.let { child ->
            packer.packString("prop")
            MessagePackText.write(packer, child)
        }
        value.sig?.let { child ->
            packer.packString("sig")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.shapeTypes?.let { child ->
            packer.packString("shapeTypes")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.argTypes?.let { child ->
            packer.packString("argTypes")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.dynRet?.let { child ->
            packer.packString("dynRet")
            writeType(child, packer, depth + 1)
        }
        value.ret?.let { child ->
            packer.packString("ret")
            writeType(child, packer, depth + 1)
        }
        packer.packString("args")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.args.size)
        for (item0 in value.args) {
            write(item0, packer, depth + 1 + 1)
        }
        value.suspendCall?.let { child ->
            packer.packString("suspendCall")
            packer.packBoolean(child)
        }
        value.clrAwaitBridge?.let { child ->
            packer.packString("clrAwaitBridge")
            packer.packBoolean(child)
        }
        value.awaitResult?.let { child ->
            packer.packString("awaitResult")
            writeType(child, packer, depth + 1)
        }
        value.overrides?.let { child ->
            packer.packString("overrides")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirOverride(item0, packer, depth + 1 + 1)
            }
        }
        value.anySlot?.let { child ->
            packer.packString("anySlot")
            packer.packBoolean(child)
        }
        value.superCall?.let { child ->
            packer.packString("super")
            packer.packBoolean(child)
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
        value.defaultDeclarationId?.let { child ->
            packer.packString("defaultDeclarationId")
            MessagePackText.write(packer, child)
        }
        value.defaultOwnerType?.let { child ->
            packer.packString("defaultOwnerType")
            writeType(child, packer, depth + 1)
        }
        value.dotktFrontendDeclarationConsumed?.let { child ->
            packer.packString("dotktFrontendDeclarationConsumed")
            packer.packBoolean(child)
        }
        value.clrFlagsOperation?.let { child ->
            packer.packString("clrFlagsOperation")
            MessagePackText.write(packer, child)
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberMethodTypeParams?.let { child ->
            packer.packString("memberMethodTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberSignature?.let { child ->
            packer.packString("memberSignature")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.memberReturnType?.let { child ->
            packer.packString("memberReturnType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirCallLocal(value: BirCallLocal, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.sig == null) 0 else 1) + (if (value.typeArgs == null) 0 else 1) + (if (value.ret == null) 0 else 1) + (if (value.sty == null) 0 else 1) + (if (value.suspendCall == null) 0 else 1))
        packer.packString("k")
        packer.packString("callLocal")
        packer.packString("id")
        MessagePackText.write(packer, value.id)
        value.sig?.let { child ->
            packer.packString("sig")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("args")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.args.size)
        for (item0 in value.args) {
            write(item0, packer, depth + 1 + 1)
        }
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.ret?.let { child ->
            packer.packString("ret")
            writeType(child, packer, depth + 1)
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
        value.suspendCall?.let { child ->
            packer.packString("suspendCall")
            packer.packBoolean(child)
        }
    }

    private fun writeBirFieldGet(value: BirFieldGet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.companionCall == null) 0 else 1) + (if (value.outer == null) 0 else 1) + (if (value.dataClassCopyDefault == null) 0 else 1) + (if (value.generatedEqualsFieldRead == null) 0 else 1) + (if (value.ret == null) 0 else 1) + (if (value.sty == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberType == null) 0 else 1))
        packer.packString("k")
        packer.packString("field")
        value.companionCall?.let { child ->
            packer.packString("companionCall")
            packer.packBoolean(child)
        }
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("recv")
        write(value.recv, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
        value.dataClassCopyDefault?.let { child ->
            packer.packString("dataClassCopyDefault")
            packer.packBoolean(child)
        }
        value.generatedEqualsFieldRead?.let { child ->
            packer.packString("generatedEqualsFieldRead")
            packer.packBoolean(child)
        }
        value.ret?.let { child ->
            packer.packString("ret")
            writeType(child, packer, depth + 1)
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberType?.let { child ->
            packer.packString("memberType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirStaticFieldGet(value: BirStaticFieldGet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.companionCall == null) 0 else 1) + (if (value.ret == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.sty == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberType == null) 0 else 1))
        packer.packString("k")
        packer.packString("staticField")
        value.companionCall?.let { child ->
            packer.packString("companionCall")
            packer.packBoolean(child)
        }
        value.ret?.let { child ->
            packer.packString("ret")
            writeType(child, packer, depth + 1)
        }
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberType?.let { child ->
            packer.packString("memberType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirFieldSetExpression(value: BirFieldSetExpression, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5 + (if (value.companionCall == null) 0 else 1) + (if (value.outer == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberType == null) 0 else 1))
        packer.packString("k")
        packer.packString("setFieldExpr")
        value.companionCall?.let { child ->
            packer.packString("companionCall")
            packer.packBoolean(child)
        }
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("recv")
        write(value.recv, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("value")
        write(value.value, packer, depth + 1)
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberType?.let { child ->
            packer.packString("memberType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirStaticFieldSet(value: BirStaticFieldSet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.companionCall == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberType == null) 0 else 1))
        packer.packString("k")
        packer.packString("staticFieldSet")
        value.companionCall?.let { child ->
            packer.packString("companionCall")
            packer.packBoolean(child)
        }
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("value")
        write(value.value, packer, depth + 1)
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberType?.let { child ->
            packer.packString("memberType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirLateinitGet(value: BirLateinitGet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.static == null) 0 else 1) + (if (value.recv == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.sty == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberType == null) 0 else 1))
        packer.packString("k")
        packer.packString("lateinitGet")
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        value.static?.let { child ->
            packer.packString("static")
            packer.packBoolean(child)
        }
        value.recv?.let { child ->
            packer.packString("recv")
            write(child, packer, depth + 1)
        }
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberType?.let { child ->
            packer.packString("memberType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirLateinitIsInitialized(value: BirLateinitIsInitialized, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.ownerType == null) 0 else 1) + (if (value.recv == null) 0 else 1))
        packer.packString("k")
        packer.packString("lateinitIsInitialized")
        packer.packString("lateinitDeclarationId")
        MessagePackText.write(packer, value.lateinitDeclarationId)
        value.ownerType?.let { child ->
            packer.packString("ownerType")
            writeType(child, packer, depth + 1)
        }
        value.recv?.let { child ->
            packer.packString("recv")
            write(child, packer, depth + 1)
        }
    }

    private fun writeBirModifiers(value: BirModifiers, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(0 + (if (value.isInline == null) 0 else 1) + (if (value.isInfix == null) 0 else 1) + (if (value.isOperator == null) 0 else 1) + (if (value.isTailrec == null) 0 else 1) + (if (value.isExternal == null) 0 else 1) + (if (value.isExtension == null) 0 else 1) + (if (value.isOverride == null) 0 else 1) + (if (value.isAbstract == null) 0 else 1) + (if (value.isOpen == null) 0 else 1) + (if (value.isSuspend == null) 0 else 1) + (if (value.isInlineOnly == null) 0 else 1) + (if (value.isVararg == null) 0 else 1) + (if (value.isNoinline == null) 0 else 1) + (if (value.isCrossinline == null) 0 else 1) + (if (value.isContext == null) 0 else 1) + (if (value.isExtensionReceiver == null) 0 else 1) + (if (value.isAnnotation == null) 0 else 1) + (if (value.isFun == null) 0 else 1) + (if (value.isSealed == null) 0 else 1) + (if (value.isValue == null) 0 else 1) + (if (value.isObject == null) 0 else 1) + (if (value.isInner == null) 0 else 1))
        value.isInline?.let { child ->
            packer.packString("inline")
            packer.packBoolean(child)
        }
        value.isInfix?.let { child ->
            packer.packString("infix")
            packer.packBoolean(child)
        }
        value.isOperator?.let { child ->
            packer.packString("operator")
            packer.packBoolean(child)
        }
        value.isTailrec?.let { child ->
            packer.packString("tailrec")
            packer.packBoolean(child)
        }
        value.isExternal?.let { child ->
            packer.packString("external")
            packer.packBoolean(child)
        }
        value.isExtension?.let { child ->
            packer.packString("ext")
            packer.packBoolean(child)
        }
        value.isOverride?.let { child ->
            packer.packString("override")
            packer.packBoolean(child)
        }
        value.isAbstract?.let { child ->
            packer.packString("abstract")
            packer.packBoolean(child)
        }
        value.isOpen?.let { child ->
            packer.packString("open")
            packer.packBoolean(child)
        }
        value.isSuspend?.let { child ->
            packer.packString("suspend")
            packer.packBoolean(child)
        }
        value.isInlineOnly?.let { child ->
            packer.packString("inlineOnly")
            packer.packBoolean(child)
        }
        value.isVararg?.let { child ->
            packer.packString("vararg")
            packer.packBoolean(child)
        }
        value.isNoinline?.let { child ->
            packer.packString("noinline")
            packer.packBoolean(child)
        }
        value.isCrossinline?.let { child ->
            packer.packString("crossinline")
            packer.packBoolean(child)
        }
        value.isContext?.let { child ->
            packer.packString("context")
            packer.packBoolean(child)
        }
        value.isExtensionReceiver?.let { child ->
            packer.packString("extensionReceiver")
            packer.packBoolean(child)
        }
        value.isAnnotation?.let { child ->
            packer.packString("annotation")
            packer.packBoolean(child)
        }
        value.isFun?.let { child ->
            packer.packString("fun")
            packer.packBoolean(child)
        }
        value.isSealed?.let { child ->
            packer.packString("sealed")
            packer.packBoolean(child)
        }
        value.isValue?.let { child ->
            packer.packString("value")
            packer.packBoolean(child)
        }
        value.isObject?.let { child ->
            packer.packString("object")
            packer.packBoolean(child)
        }
        value.isInner?.let { child ->
            packer.packString("inner")
            packer.packBoolean(child)
        }
    }

    private fun writeBirDefaultBody(value: BirDefaultBody, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("index")
        packer.packInt(value.index)
        packer.packString("body")
        write(value.body, packer, depth + 1)
    }

    private fun writeBirAttributeNamedArgument(value: BirAttributeNamedArgument, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("kind")
        MessagePackText.write(packer, value.kind)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("value")
        write(value.value, packer, depth + 1)
    }

    private fun writeBirAttribute(value: BirAttribute, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.attrClr == null) 0 else 1) + (if (value.namedArgs == null) 0 else 1))
        packer.packString("attr")
        writeType(value.attr, packer, depth + 1)
        value.attrClr?.let { child ->
            packer.packString("attrClr")
            packer.packBoolean(child)
        }
        packer.packString("argTypes")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.argTypes.size)
        for (item0 in value.argTypes) {
            writeType(item0, packer, depth + 1 + 1)
        }
        packer.packString("args")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.args.size)
        for (item0 in value.args) {
            write(item0, packer, depth + 1 + 1)
        }
        value.namedArgs?.let { child ->
            packer.packString("namedArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirAttributeNamedArgument(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirParameter(value: BirParameter, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.outer == null) 0 else 1) + (if (value.mods == null) 0 else 1) + (if (value.defaultValue == null) 0 else 1) + (if (value.defaultBir == null) 0 else 1) + (if (value.ctxFnType == null) 0 else 1) + (if (value.sharedCellType == null) 0 else 1) + (if (value.attrs == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
        value.mods?.let { child ->
            packer.packString("mods")
            writeBirModifiers(child, packer, depth + 1)
        }
        value.defaultValue?.let { child ->
            packer.packString("default")
            write(child, packer, depth + 1)
        }
        value.defaultBir?.let { child ->
            packer.packString("defaultBir")
            writeBirDefaultBody(child, packer, depth + 1)
        }
        value.ctxFnType?.let { child ->
            packer.packString("ctxFnType")
            packer.packInt(child)
        }
        value.sharedCellType?.let { child ->
            packer.packString("sharedCellType")
            writeType(child, packer, depth + 1)
        }
        value.attrs?.let { child ->
            packer.packString("attrs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirAttribute(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirDefaultCarrier(value: BirDefaultCarrier, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("defaultCarrier")
        packer.packString("expr")
        write(value.expr, packer, depth + 1)
        packer.packString("lifted")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.lifted.size)
        for (item0 in value.lifted) {
            writeBirFunctionDeclaration(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirLocalFunction(value: BirLocalFunction, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("localFun")
        packer.packString("id")
        MessagePackText.write(packer, value.id)
        packer.packString("decl")
        writeBirFunctionDeclaration(value.decl, packer, depth + 1)
    }

    private fun writeBirInheritedImplementation(value: BirInheritedImplementation, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5 + (if (value.sig == null) 0 else 1))
        packer.packString("owner")
        writeType(value.owner, packer, depth + 1)
        packer.packString("member")
        MessagePackText.write(packer, value.member)
        packer.packString("kind")
        MessagePackText.write(packer, value.kind)
        packer.packString("arity")
        packer.packInt(value.arity)
        packer.packString("typeParams")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.typeParams.size)
        for (item0 in value.typeParams) {
            writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
        }
        value.sig?.let { child ->
            packer.packString("sig")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirDeclarationPosition(value: BirDeclarationPosition, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1 + (if (value.l == null) 0 else 1) + (if (value.c == null) 0 else 1))
        packer.packString("f")
        MessagePackText.write(packer, value.f)
        value.l?.let { child ->
            packer.packString("l")
            packer.packInt(child)
        }
        value.c?.let { child ->
            packer.packString("c")
            packer.packInt(child)
        }
    }

    private fun writeBirFunctionDeclaration(value: BirFunctionDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.name == null) 0 else 1) + (if (value.sourceName == null) 0 else 1) + (if (value.isStatic == null) 0 else 1) + (if (value.isOverride == null) 0 else 1) + (if (value.isVirtual == null) 0 else 1) + (if (value.generated == null) 0 else 1) + (if (value.typeParams == null) 0 else 1) + (if (value.semanticOwner == null) 0 else 1) + (if (value.declarationId == null) 0 else 1) + (if (value.mods == null) 0 else 1) + (if (value.suspendRet == null) 0 else 1) + (if (value.overrides == null) 0 else 1) + (if (value.explicitClrName == null) 0 else 1) + (if (value.kotlinStatic == null) 0 else 1) + (if (value.isAbstract == null) 0 else 1) + (if (value.objectOverride == null) 0 else 1) + (if (value.vis == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.companionMemberKind == null) 0 else 1) + (if (value.companionSourceName == null) 0 else 1) + (if (value.retCtxFnType == null) 0 else 1) + (if (value.attrs == null) 0 else 1) + (if (value.pos == null) 0 else 1) + (if (value.propertyName == null) 0 else 1) + (if (value.propertyAccessor == null) 0 else 1) + (if (value.propertyAssociation == null) 0 else 1) + (if (value.eventProperty == null) 0 else 1) + (if (value.fakeOverride == null) 0 else 1) + (if (value.inheritedImplementation == null) 0 else 1) + (if (value.syntheticTypeArgs == null) 0 else 1))
        value.name?.let { child ->
            packer.packString("name")
            MessagePackText.write(packer, child)
        }
        value.sourceName?.let { child ->
            packer.packString("sourceName")
            MessagePackText.write(packer, child)
        }
        value.isStatic?.let { child ->
            packer.packString("static")
            packer.packBoolean(child)
        }
        value.isOverride?.let { child ->
            packer.packString("override")
            packer.packBoolean(child)
        }
        value.isVirtual?.let { child ->
            packer.packString("virtual")
            packer.packBoolean(child)
        }
        value.generated?.let { child ->
            packer.packString("generated")
            packer.packBoolean(child)
        }
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeBirParameter(item0, packer, depth + 1 + 1)
        }
        packer.packString("ret")
        writeType(value.ret, packer, depth + 1)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
        value.typeParams?.let { child ->
            packer.packString("typeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.semanticOwner?.let { child ->
            packer.packString("semanticOwner")
            MessagePackText.write(packer, child)
        }
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
        value.mods?.let { child ->
            packer.packString("mods")
            writeBirModifiers(child, packer, depth + 1)
        }
        value.suspendRet?.let { child ->
            packer.packString("suspendRet")
            writeType(child, packer, depth + 1)
        }
        value.overrides?.let { child ->
            packer.packString("overrides")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirOverride(item0, packer, depth + 1 + 1)
            }
        }
        value.explicitClrName?.let { child ->
            packer.packString("explicitClrName")
            MessagePackText.write(packer, child)
        }
        value.kotlinStatic?.let { child ->
            packer.packString("kotlinStatic")
            packer.packBoolean(child)
        }
        value.isAbstract?.let { child ->
            packer.packString("abstract")
            packer.packBoolean(child)
        }
        value.objectOverride?.let { child ->
            packer.packString("objectOverride")
            packer.packBoolean(child)
        }
        value.vis?.let { child ->
            packer.packString("vis")
            MessagePackText.write(packer, child)
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.companionMemberKind?.let { child ->
            packer.packString("companionMemberKind")
            MessagePackText.write(packer, child)
        }
        value.companionSourceName?.let { child ->
            packer.packString("companionSourceName")
            MessagePackText.write(packer, child)
        }
        value.retCtxFnType?.let { child ->
            packer.packString("retCtxFnType")
            packer.packInt(child)
        }
        value.attrs?.let { child ->
            packer.packString("attrs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirAttribute(item0, packer, depth + 1 + 1)
            }
        }
        value.pos?.let { child ->
            packer.packString("pos")
            writeBirDeclarationPosition(child, packer, depth + 1)
        }
        value.propertyName?.let { child ->
            packer.packString("propertyName")
            MessagePackText.write(packer, child)
        }
        value.propertyAccessor?.let { child ->
            packer.packString("propertyAccessor")
            MessagePackText.write(packer, child)
        }
        value.propertyAssociation?.let { child ->
            packer.packString("propertyAssociation")
            MessagePackText.write(packer, child)
        }
        value.eventProperty?.let { child ->
            packer.packString("eventProperty")
            packer.packBoolean(child)
        }
        value.fakeOverride?.let { child ->
            packer.packString("fakeOverride")
            packer.packBoolean(child)
        }
        value.inheritedImplementation?.let { child ->
            packer.packString("inheritedImplementation")
            writeBirInheritedImplementation(child, packer, depth + 1)
        }
        value.syntheticTypeArgs?.let { child ->
            packer.packString("_syntheticTypeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirConstructor(value: BirConstructor, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.delegationSig == null) 0 else 1) + (if (value.thisArgs == BirField.Absent) 0 else 1) + (if (value.delegationBindings == null) 0 else 1) + (if (value.delegationOuterSlot == null) 0 else 1) + (if (value.delegationDeclarationId == null) 0 else 1) + (if (value.declarationId == null) 0 else 1) + (if (value.declarationSourceName == null) 0 else 1) + (if (value.vis == null) 0 else 1) + (if (value.attrs == null) 0 else 1))
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeBirParameter(item0, packer, depth + 1 + 1)
        }
        packer.packString("baseArgs")
        if (value.baseArgs == null) packer.packNil()
        else {
            checkDepth(depth + 1)
            packer.packArrayHeader(value.baseArgs.size)
            for (item0 in value.baseArgs) {
                write(item0, packer, depth + 1 + 1)
            }
        }
        value.delegationSig?.let { child ->
            packer.packString("delegationSig")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        when (val present = value.thisArgs) {
            BirField.Absent -> {}
            BirField.Null -> {
                packer.packString("thisArgs")
                packer.packNil()
            }
            is BirField.Value -> {
                packer.packString("thisArgs")
                checkDepth(depth + 1)
                packer.packArrayHeader(present.value.size)
                for (item0 in present.value) {
                    write(item0, packer, depth + 1 + 1)
                }
            }
        }
        value.delegationBindings?.let { child ->
            packer.packString("delegationBindings")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirCallBinding(item0, packer, depth + 1 + 1)
            }
        }
        value.delegationOuterSlot?.let { child ->
            packer.packString("delegationOuterSlot")
            packer.packInt(child)
        }
        value.delegationDeclarationId?.let { child ->
            packer.packString("delegationDeclarationId")
            MessagePackText.write(packer, child)
        }
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
        value.declarationSourceName?.let { child ->
            packer.packString("declarationSourceName")
            MessagePackText.write(packer, child)
        }
        value.vis?.let { child ->
            packer.packString("vis")
            MessagePackText.write(packer, child)
        }
        value.attrs?.let { child ->
            packer.packString("attrs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirAttribute(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirFieldDeclaration(value: BirFieldDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.isStatic == null) 0 else 1) + (if (value.init == BirField.Absent) 0 else 1) + (if (value.vis == null) 0 else 1) + (if (value.readOnly == null) 0 else 1) + (if (value.initOnly == null) 0 else 1) + (if (value.isConst == null) 0 else 1) + (if (value.kotlinStatic == null) 0 else 1) + (if (value.outer == null) 0 else 1) + (if (value.isLateinit == null) 0 else 1) + (if (value.lateinitDeclarationId == null) 0 else 1) + (if (value.isVolatile == null) 0 else 1) + (if (value.attrs == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.companionMemberKind == null) 0 else 1) + (if (value.companionSourceName == null) 0 else 1) + (if (value.companionPropertyMutable == null) 0 else 1) + (if (value.companionStorageReadOnly == null) 0 else 1) + (if (value.companionSetterVisibility == null) 0 else 1) + (if (value.companionGetterExplicitClrName == null) 0 else 1) + (if (value.companionSetterExplicitClrName == null) 0 else 1) + (if (value.companionGetterDeclarationId == null) 0 else 1) + (if (value.companionSetterDeclarationId == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.isStatic?.let { child ->
            packer.packString("static")
            packer.packBoolean(child)
        }
        when (val present = value.init) {
            BirField.Absent -> {}
            BirField.Null -> {
                packer.packString("init")
                packer.packNil()
            }
            is BirField.Value -> {
                packer.packString("init")
                write(present.value, packer, depth + 1)
            }
        }
        value.vis?.let { child ->
            packer.packString("vis")
            MessagePackText.write(packer, child)
        }
        value.readOnly?.let { child ->
            packer.packString("readOnly")
            packer.packBoolean(child)
        }
        value.initOnly?.let { child ->
            packer.packString("initOnly")
            packer.packBoolean(child)
        }
        value.isConst?.let { child ->
            packer.packString("const")
            packer.packBoolean(child)
        }
        value.kotlinStatic?.let { child ->
            packer.packString("kotlinStatic")
            packer.packBoolean(child)
        }
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
        value.isLateinit?.let { child ->
            packer.packString("lateinit")
            packer.packBoolean(child)
        }
        value.lateinitDeclarationId?.let { child ->
            packer.packString("lateinitDeclarationId")
            MessagePackText.write(packer, child)
        }
        value.isVolatile?.let { child ->
            packer.packString("volatile")
            packer.packBoolean(child)
        }
        value.attrs?.let { child ->
            packer.packString("attrs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirAttribute(item0, packer, depth + 1 + 1)
            }
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.companionMemberKind?.let { child ->
            packer.packString("companionMemberKind")
            MessagePackText.write(packer, child)
        }
        value.companionSourceName?.let { child ->
            packer.packString("companionSourceName")
            MessagePackText.write(packer, child)
        }
        value.companionPropertyMutable?.let { child ->
            packer.packString("companionPropertyMutable")
            packer.packBoolean(child)
        }
        value.companionStorageReadOnly?.let { child ->
            packer.packString("companionStorageReadOnly")
            packer.packBoolean(child)
        }
        value.companionSetterVisibility?.let { child ->
            packer.packString("companionSetterVisibility")
            MessagePackText.write(packer, child)
        }
        value.companionGetterExplicitClrName?.let { child ->
            packer.packString("companionGetterExplicitClrName")
            MessagePackText.write(packer, child)
        }
        value.companionSetterExplicitClrName?.let { child ->
            packer.packString("companionSetterExplicitClrName")
            MessagePackText.write(packer, child)
        }
        value.companionGetterDeclarationId?.let { child ->
            packer.packString("companionGetterDeclarationId")
            MessagePackText.write(packer, child)
        }
        value.companionSetterDeclarationId?.let { child ->
            packer.packString("companionSetterDeclarationId")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirRefTypeDeclaration(value: BirRefTypeDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("typeParams")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.typeParams.size)
        for (item0 in value.typeParams) {
            writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirPropertyDeclaration(value: BirPropertyDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.kotlinStatic == null) 0 else 1) + (if (value.overrides == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("kotlinAccessors")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.kotlinAccessors.size)
        for (item0 in value.kotlinAccessors) {
            MessagePackText.write(packer, item0)
        }
        packer.packString("propertyAssociation")
        MessagePackText.write(packer, value.propertyAssociation)
        value.kotlinStatic?.let { child ->
            packer.packString("kotlinStatic")
            packer.packBoolean(child)
        }
        value.overrides?.let { child ->
            packer.packString("overrides")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirOverride(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirInterfaceDefinition(value: BirInterfaceDefinition, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(9 + (if (value.semanticOwner == null) 0 else 1) + (if (value.vis == null) 0 else 1) + (if (value.mods == null) 0 else 1) + (if (value.typeParams == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("kind")
        MessagePackText.write(packer, "interface")
        value.semanticOwner?.let { child ->
            packer.packString("semanticOwner")
            MessagePackText.write(packer, child)
        }
        value.vis?.let { child ->
            packer.packString("vis")
            MessagePackText.write(packer, child)
        }
        value.mods?.let { child ->
            packer.packString("mods")
            writeBirModifiers(child, packer, depth + 1)
        }
        value.typeParams?.let { child ->
            packer.packString("typeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("base")
        if (value.base == null) packer.packNil()
        else {
            writeType(value.base, packer, depth + 1)
        }
        packer.packString("interfaces")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.interfaces.size)
        for (item0 in value.interfaces) {
            writeType(item0, packer, depth + 1 + 1)
        }
        packer.packString("fields")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.fields.size)
        for (item0 in value.fields) {
            writeBirFieldDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("ctors")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.ctors.size)
        for (item0 in value.ctors) {
            writeBirConstructor(item0, packer, depth + 1 + 1)
        }
        packer.packString("methods")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.methods.size)
        for (item0 in value.methods) {
            writeBirFunctionDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("properties")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.properties.size)
        for (item0 in value.properties) {
            writeBirPropertyDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("attrs")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.attrs.size)
        for (item0 in value.attrs) {
            writeBirAttribute(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirEnumEntryDeclaration(value: BirEnumEntryDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.value == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("ordinal")
        packer.packInt(value.ordinal)
        value.value?.let { child ->
            packer.packString("value")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirClrEnumDeclaration(value: BirClrEnumDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1)
        packer.packString("underlying")
        writeType(value.underlying, packer, depth + 1)
    }

    private fun writeBirEnumDefinition(value: BirEnumDefinition, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.semanticOwner == null) 0 else 1) + (if (value.vis == null) 0 else 1) + (if (value.clrEnum == null) 0 else 1) + (if (value.pos == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("kind")
        MessagePackText.write(packer, "enum")
        value.semanticOwner?.let { child ->
            packer.packString("semanticOwner")
            MessagePackText.write(packer, child)
        }
        value.vis?.let { child ->
            packer.packString("vis")
            MessagePackText.write(packer, child)
        }
        value.clrEnum?.let { child ->
            packer.packString("clrEnum")
            writeBirClrEnumDeclaration(child, packer, depth + 1)
        }
        packer.packString("entries")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.entries.size)
        for (item0 in value.entries) {
            writeBirEnumEntryDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("attrs")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.attrs.size)
        for (item0 in value.attrs) {
            writeBirAttribute(item0, packer, depth + 1 + 1)
        }
        value.pos?.let { child ->
            packer.packString("pos")
            writeBirDeclarationPosition(child, packer, depth + 1)
        }
    }

    private fun writeBirClassDefinition(value: BirClassDefinition, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(7 + (if (value.generated == null) 0 else 1) + (if (value.companionCaptureOwner == null) 0 else 1) + (if (value.typeParams == null) 0 else 1) + (if (value.semanticOwner == null) 0 else 1) + (if (value.isAbstract == null) 0 else 1) + (if (value.vis == null) 0 else 1) + (if (value.mods == null) 0 else 1) + (if (value.properties == null) 0 else 1) + (if (value.attrs == null) 0 else 1) + (if (value.pos == null) 0 else 1) + (if (value.staticSemanticOwner == null) 0 else 1) + (if (value.outerTypeParamCount == null) 0 else 1) + (if (value.outerTypeParamOffset == null) 0 else 1) + (if (value.kotlinCompanion == null) 0 else 1) + (if (value.inheritedDefaultAccessors == null) 0 else 1) + (if (value.inheritedDefaultMethods == null) 0 else 1) + (if (value.inheritedClassMethods == null) 0 else 1) + (if (value.clrEvents == null) 0 else 1) + (if (value.clrEventForwarders == null) 0 else 1) + (if (value.enumRich == null) 0 else 1) + (if (value.richEnum == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("kind")
        MessagePackText.write(packer, "class")
        value.generated?.let { child ->
            packer.packString("generated")
            packer.packBoolean(child)
        }
        packer.packString("base")
        if (value.base == null) packer.packNil()
        else {
            writeType(value.base, packer, depth + 1)
        }
        value.companionCaptureOwner?.let { child ->
            packer.packString("companionCaptureOwner")
            MessagePackText.write(packer, child)
        }
        value.typeParams?.let { child ->
            packer.packString("typeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.semanticOwner?.let { child ->
            packer.packString("semanticOwner")
            MessagePackText.write(packer, child)
        }
        packer.packString("interfaces")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.interfaces.size)
        for (item0 in value.interfaces) {
            writeType(item0, packer, depth + 1 + 1)
        }
        packer.packString("fields")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.fields.size)
        for (item0 in value.fields) {
            writeBirFieldDeclaration(item0, packer, depth + 1 + 1)
        }
        packer.packString("ctors")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.ctors.size)
        for (item0 in value.ctors) {
            writeBirConstructor(item0, packer, depth + 1 + 1)
        }
        packer.packString("methods")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.methods.size)
        for (item0 in value.methods) {
            writeBirFunctionDeclaration(item0, packer, depth + 1 + 1)
        }
        value.isAbstract?.let { child ->
            packer.packString("abstract")
            packer.packBoolean(child)
        }
        value.vis?.let { child ->
            packer.packString("vis")
            MessagePackText.write(packer, child)
        }
        value.mods?.let { child ->
            packer.packString("mods")
            writeBirModifiers(child, packer, depth + 1)
        }
        value.properties?.let { child ->
            packer.packString("properties")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirPropertyDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.attrs?.let { child ->
            packer.packString("attrs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirAttribute(item0, packer, depth + 1 + 1)
            }
        }
        value.pos?.let { child ->
            packer.packString("pos")
            writeBirDeclarationPosition(child, packer, depth + 1)
        }
        value.staticSemanticOwner?.let { child ->
            packer.packString("staticSemanticOwner")
            MessagePackText.write(packer, child)
        }
        value.outerTypeParamCount?.let { child ->
            packer.packString("outerTypeParamCount")
            packer.packInt(child)
        }
        value.outerTypeParamOffset?.let { child ->
            packer.packString("outerTypeParamOffset")
            packer.packInt(child)
        }
        value.kotlinCompanion?.let { child ->
            packer.packString("kotlinCompanion")
            writeBirKotlinCompanion(child, packer, depth + 1)
        }
        value.inheritedDefaultAccessors?.let { child ->
            packer.packString("inheritedDefaultAccessors")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirInheritedDefaultAccessor(item0, packer, depth + 1 + 1)
            }
        }
        value.inheritedDefaultMethods?.let { child ->
            packer.packString("inheritedDefaultMethods")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirInheritedDefaultMethod(item0, packer, depth + 1 + 1)
            }
        }
        value.inheritedClassMethods?.let { child ->
            packer.packString("inheritedClassMethods")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirInheritedClassMethod(item0, packer, depth + 1 + 1)
            }
        }
        value.clrEvents?.let { child ->
            packer.packString("clrEvents")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirClrEventBacking(item0, packer, depth + 1 + 1)
            }
        }
        value.clrEventForwarders?.let { child ->
            packer.packString("clrEventForwarders")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirClrEventForwarder(item0, packer, depth + 1 + 1)
            }
        }
        value.enumRich?.let { child ->
            packer.packString("enumRich")
            packer.packBoolean(child)
        }
        value.richEnum?.let { child ->
            packer.packString("richEnum")
            writeBirRichEnumDeclaration(child, packer, depth + 1)
        }
    }

    private fun writeBirKotlinCompanion(value: BirKotlinCompanion, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("owner")
        MessagePackText.write(packer, value.owner)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("visibility")
        MessagePackText.write(packer, value.visibility)
    }

    private fun writeBirRichEnumEntry(value: BirRichEnumEntry, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("field")
        MessagePackText.write(packer, value.field)
    }

    private fun writeBirRichEnumDeclaration(value: BirRichEnumDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5)
        packer.packString("entries")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.entries.size)
        for (item0 in value.entries) {
            writeBirRichEnumEntry(item0, packer, depth + 1 + 1)
        }
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("ordinal")
        MessagePackText.write(packer, value.ordinal)
        packer.packString("values")
        MessagePackText.write(packer, value.values)
        packer.packString("valueOf")
        MessagePackText.write(packer, value.valueOf)
    }

    private fun writeBirInheritedDefaultAccessor(value: BirInheritedDefaultAccessor, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5 + (if (value.overrides == null) 0 else 1))
        packer.packString("propertyName")
        MessagePackText.write(packer, value.propertyName)
        packer.packString("propertyAccessor")
        MessagePackText.write(packer, value.propertyAccessor)
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeType(item0, packer, depth + 1 + 1)
        }
        packer.packString("ret")
        writeType(value.ret, packer, depth + 1)
        value.overrides?.let { child ->
            packer.packString("overrides")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirOverride(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("implementation")
        writeBirInheritedImplementation(value.implementation, packer, depth + 1)
    }

    private fun writeBirInheritedDefaultMethod(value: BirInheritedDefaultMethod, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.overrides == null) 0 else 1))
        packer.packString("member")
        MessagePackText.write(packer, value.member)
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeType(item0, packer, depth + 1 + 1)
        }
        packer.packString("ret")
        writeType(value.ret, packer, depth + 1)
        value.overrides?.let { child ->
            packer.packString("overrides")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirOverride(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("implementation")
        writeBirInheritedImplementation(value.implementation, packer, depth + 1)
    }

    private fun writeBirInheritedClassMethod(value: BirInheritedClassMethod, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.propertyName == null) 0 else 1) + (if (value.propertyAccessor == null) 0 else 1) + (if (value.propertyAssociation == null) 0 else 1) + (if (value.typeParams == null) 0 else 1) + (if (value.mods == null) 0 else 1) + (if (value.overrides == null) 0 else 1))
        packer.packString("member")
        MessagePackText.write(packer, value.member)
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeBirParameter(item0, packer, depth + 1 + 1)
        }
        packer.packString("ret")
        writeType(value.ret, packer, depth + 1)
        value.propertyName?.let { child ->
            packer.packString("propertyName")
            MessagePackText.write(packer, child)
        }
        value.propertyAccessor?.let { child ->
            packer.packString("propertyAccessor")
            MessagePackText.write(packer, child)
        }
        value.propertyAssociation?.let { child ->
            packer.packString("propertyAssociation")
            MessagePackText.write(packer, child)
        }
        value.typeParams?.let { child ->
            packer.packString("typeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.mods?.let { child ->
            packer.packString("mods")
            writeBirModifiers(child, packer, depth + 1)
        }
        value.overrides?.let { child ->
            packer.packString("overrides")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirOverride(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("inheritedImplementation")
        writeBirInheritedImplementation(value.inheritedImplementation, packer, depth + 1)
    }

    private fun writeBirClrEventBacking(value: BirClrEventBacking, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(6 + (if (value.classOverrideOwner == null) 0 else 1) + (if (value.policyField == null) 0 else 1))
        packer.packString("k")
        MessagePackText.write(packer, "clrEventBacking")
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("static")
        packer.packBoolean(value.isStatic)
        packer.packString("propertyAssociation")
        MessagePackText.write(packer, value.propertyAssociation)
        packer.packString("handlerType")
        writeType(value.handlerType, packer, depth + 1)
        packer.packString("modality")
        MessagePackText.write(packer, value.modality)
        value.classOverrideOwner?.let { child ->
            packer.packString("classOverrideOwner")
            writeType(child, packer, depth + 1)
        }
        value.policyField?.let { child ->
            packer.packString("policyField")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirClrEventForwarder(value: BirClrEventForwarder, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("propertyAssociation")
        MessagePackText.write(packer, value.propertyAssociation)
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("recv")
        write(value.recv, packer, depth + 1)
    }

    private fun writeBirClrEventAccessor(value: BirClrEventAccessor, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("clrEventAccessor")
        packer.packString("kind")
        MessagePackText.write(packer, value.kind)
        packer.packString("event")
        MessagePackText.write(packer, value.event)
    }

    private fun writeBirNewSam(value: BirNewSam, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.typeArgs == null) 0 else 1))
        packer.packString("k")
        packer.packString("newSam")
        packer.packString("samType")
        writeType(value.samType, packer, depth + 1)
        packer.packString("captures")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.captures.size)
        for (item0 in value.captures) {
            write(item0, packer, depth + 1 + 1)
        }
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("synthClass")
        writeBirClassDefinition(value.synthClass, packer, depth + 1)
    }

    private fun writeBirSamConversion(value: BirSamConversion, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("samConvert")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("funcType")
        writeType(value.funcType, packer, depth + 1)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirCaptureDescriptor(value: BirCaptureDescriptor, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.outer == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
    }

    private fun writeBirClosureDefinition(value: BirClosureDefinition, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5 + (if (value.typeParams == null) 0 else 1) + (if (value.semanticOwner == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("fields")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.fields.size)
        for (item0 in value.fields) {
            writeBirCaptureDescriptor(item0, packer, depth + 1 + 1)
        }
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeBirLambdaParameter(item0, packer, depth + 1 + 1)
        }
        packer.packString("ret")
        writeType(value.ret, packer, depth + 1)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
        value.typeParams?.let { child ->
            packer.packString("typeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.semanticOwner?.let { child ->
            packer.packString("semanticOwner")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirNewClosure(value: BirNewClosure, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(6 + (if (value.typeArgs == null) 0 else 1))
        packer.packString("k")
        packer.packString("newClosure")
        packer.packString("closureType")
        writeType(value.closureType, packer, depth + 1)
        packer.packString("captures")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.captures.size)
        for (item0 in value.captures) {
            write(item0, packer, depth + 1 + 1)
        }
        packer.packString("method")
        MessagePackText.write(packer, value.method)
        packer.packString("funcType")
        writeType(value.funcType, packer, depth + 1)
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("synthClass")
        writeBirClosureDefinition(value.synthClass, packer, depth + 1)
    }

    private fun writeBirNewDelegate(value: BirNewDelegate, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.sig == null) 0 else 1) + (if (value.typeArgs == null) 0 else 1) + (if (value.calleeOwner == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.declarationId == null) 0 else 1))
        packer.packString("k")
        packer.packString("newDelegate")
        packer.packString("method")
        MessagePackText.write(packer, value.method)
        packer.packString("funcType")
        writeType(value.funcType, packer, depth + 1)
        value.sig?.let { child ->
            packer.packString("sig")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.calleeOwner?.let { child ->
            packer.packString("calleeOwner")
            writeType(child, packer, depth + 1)
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirNewSuspendLambda(value: BirNewSuspendLambda, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(8 + (if (value.capValues == null) 0 else 1) + (if (value.typeArgs == null) 0 else 1) + (if (value.externalCompanionOwner == null) 0 else 1) + (if (value.companionCaptureOwner == null) 0 else 1))
        packer.packString("k")
        packer.packString("newSuspendLambda")
        packer.packString("arity")
        packer.packInt(value.arity)
        packer.packString("captures")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.captures.size)
        for (item0 in value.captures) {
            writeBirCaptureDescriptor(item0, packer, depth + 1 + 1)
        }
        value.capValues?.let { child ->
            packer.packString("capValues")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                if (item0 == null) packer.packNil()
                else {
                    write(item0, packer, depth + 1 + 1)
                }
            }
        }
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeBirLambdaParameter(item0, packer, depth + 1 + 1)
        }
        packer.packString("suspendRet")
        writeType(value.suspendRet, packer, depth + 1)
        packer.packString("typeParams")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.typeParams.size)
        for (item0 in value.typeParams) {
            writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
        }
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
        packer.packString("funcType")
        writeType(value.funcType, packer, depth + 1)
        value.externalCompanionOwner?.let { child ->
            packer.packString("externalCompanionOwner")
            MessagePackText.write(packer, child)
        }
        value.companionCaptureOwner?.let { child ->
            packer.packString("companionCaptureOwner")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirEventGet(value: BirEventGet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.companionCall == null) 0 else 1) + (if (value.recv == BirField.Absent) 0 else 1) + (if (value.sty == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.overrides == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1))
        packer.packString("k")
        packer.packString("clrEventGet")
        value.companionCall?.let { child ->
            packer.packString("companionCall")
            packer.packBoolean(child)
        }
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("static")
        packer.packBoolean(value.isStatic)
        when (val present = value.recv) {
            BirField.Absent -> {}
            BirField.Null -> {
                packer.packString("recv")
                packer.packNil()
            }
            is BirField.Value -> {
                packer.packString("recv")
                write(present.value, packer, depth + 1)
            }
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.overrides?.let { child ->
            packer.packString("overrides")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeBirOverride(item0, packer, depth + 1 + 1)
            }
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
    }

    private fun writeBirEventRaise(value: BirEventRaise, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(6 + (if (value.recv == null) 0 else 1))
        packer.packString("k")
        packer.packString("clrEventRaise")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("event")
        MessagePackText.write(packer, value.event)
        packer.packString("static")
        packer.packBoolean(value.isStatic)
        packer.packString("super")
        packer.packBoolean(value.superCall)
        value.recv?.let { child ->
            packer.packString("recv")
            write(child, packer, depth + 1)
        }
        packer.packString("args")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.args.size)
        for (item0 in value.args) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirLambdaParameter(value: BirLambdaParameter, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
    }

    private fun writeBirInlineCapture(value: BirInlineCapture, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.outer == null) 0 else 1) + (if (value.value == null) 0 else 1) + (if (value.sharedCellType == null) 0 else 1) + (if (value.sharedCellTypeParams == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
        value.value?.let { child ->
            packer.packString("value")
            write(child, packer, depth + 1)
        }
        value.sharedCellType?.let { child ->
            packer.packString("sharedCellType")
            writeType(child, packer, depth + 1)
        }
        value.sharedCellTypeParams?.let { child ->
            packer.packString("sharedCellTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirInlineLambda(value: BirInlineLambda, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5)
        packer.packString("k")
        packer.packString("inlineLambda")
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeBirLambdaParameter(item0, packer, depth + 1 + 1)
        }
        packer.packString("captures")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.captures.size)
        for (item0 in value.captures) {
            writeBirInlineCapture(item0, packer, depth + 1 + 1)
        }
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
        packer.packString("result")
        write(value.result, packer, depth + 1)
    }

    private fun writeBirStackAllocation(value: BirStackAllocation, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("stackAlloc")
        packer.packString("count")
        write(value.count, packer, depth + 1)
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
    }

    private fun writeBirStackGet(value: BirStackGet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5)
        packer.packString("k")
        packer.packString("stackGet")
        packer.packString("ptr")
        write(value.ptr, packer, depth + 1)
        packer.packString("len")
        write(value.len, packer, depth + 1)
        packer.packString("index")
        write(value.index, packer, depth + 1)
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
    }

    private fun writeBirStackSet(value: BirStackSet, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(6)
        packer.packString("k")
        packer.packString("stackSet")
        packer.packString("ptr")
        write(value.ptr, packer, depth + 1)
        packer.packString("len")
        write(value.len, packer, depth + 1)
        packer.packString("index")
        write(value.index, packer, depth + 1)
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("value")
        write(value.value, packer, depth + 1)
    }

    private fun writeBirStackAsSpan(value: BirStackAsSpan, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("stackAsSpan")
        packer.packString("ptr")
        write(value.ptr, packer, depth + 1)
        packer.packString("len")
        write(value.len, packer, depth + 1)
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
    }

    private fun writeBirInlineReceivers(value: BirInlineReceivers, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(0 + (if (value.extension == null) 0 else 1) + (if (value.dispatch == null) 0 else 1) + (if (value.dispatchTypeArgs == null) 0 else 1))
        value.extension?.let { child ->
            packer.packString("extension")
            write(child, packer, depth + 1)
        }
        value.dispatch?.let { child ->
            packer.packString("dispatch")
            write(child, packer, depth + 1)
        }
        value.dispatchTypeArgs?.let { child ->
            packer.packString("dispatchTypeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirCallInline(value: BirCallInline, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(10 + (if (value.semanticOwner == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.declarationId == null) 0 else 1))
        packer.packString("k")
        packer.packString("callInline")
        packer.packString("callee")
        writeType(value.callee, packer, depth + 1)
        packer.packString("owner")
        if (value.owner == null) packer.packNil()
        else {
            writeType(value.owner, packer, depth + 1)
        }
        packer.packString("pc")
        packer.packInt(value.pc)
        packer.packString("ga")
        packer.packInt(value.ga)
        packer.packString("typeArgs")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.typeArgs.size)
        for (item0 in value.typeArgs) {
            writeType(item0, packer, depth + 1 + 1)
        }
        packer.packString("recvs")
        writeBirInlineReceivers(value.recvs, packer, depth + 1)
        packer.packString("args")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.args.size)
        for (item0 in value.args) {
            if (item0 == null) packer.packNil()
            else {
                write(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("retType")
        writeType(value.retType, packer, depth + 1)
        packer.packString("paramSig")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.paramSig.size)
        for (item0 in value.paramSig) {
            writeType(item0, packer, depth + 1 + 1)
        }
        value.semanticOwner?.let { child ->
            packer.packString("semanticOwner")
            MessagePackText.write(packer, child)
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
    }

    private fun writeBirNewBoundDelegate(value: BirNewBoundDelegate, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5 + (if (value.typeArgs == null) 0 else 1) + (if (value.argTypes == null) 0 else 1) + (if (value.virtual == null) 0 else 1) + (if (value.anySlot == null) 0 else 1) + (if (value.calleeOwner == null) 0 else 1) + (if (value.companionReceiver == null) 0 else 1) + (if (value.declarationId == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberMethodTypeParams == null) 0 else 1) + (if (value.memberSignature == null) 0 else 1) + (if (value.memberReturnType == null) 0 else 1))
        packer.packString("k")
        packer.packString("newBoundDelegate")
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("method")
        MessagePackText.write(packer, value.method)
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.argTypes?.let { child ->
            packer.packString("argTypes")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.virtual?.let { child ->
            packer.packString("virtual")
            packer.packBoolean(child)
        }
        packer.packString("recv")
        write(value.recv, packer, depth + 1)
        packer.packString("funcType")
        writeType(value.funcType, packer, depth + 1)
        value.anySlot?.let { child ->
            packer.packString("anySlot")
            packer.packBoolean(child)
        }
        value.calleeOwner?.let { child ->
            packer.packString("calleeOwner")
            writeType(child, packer, depth + 1)
        }
        value.companionReceiver?.let { child ->
            packer.packString("companionReceiver")
            MessagePackText.write(packer, child.toJson())
        }
        value.declarationId?.let { child ->
            packer.packString("declarationId")
            MessagePackText.write(packer, child)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberMethodTypeParams?.let { child ->
            packer.packString("memberMethodTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberSignature?.let { child ->
            packer.packString("memberSignature")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.memberReturnType?.let { child ->
            packer.packString("memberReturnType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirByRefOf(value: BirByRefOf, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("byrefOf")
        packer.packString("inner")
        write(value.inner, packer, depth + 1)
    }

    private fun writeBirByRefLoad(value: BirByRefLoad, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.ptr == null) 0 else 1) + (if (value.local == null) 0 else 1))
        packer.packString("k")
        packer.packString("byrefLoad")
        value.ptr?.let { child ->
            packer.packString("ptr")
            write(child, packer, depth + 1)
        }
        value.local?.let { child ->
            packer.packString("local")
            MessagePackText.write(packer, child)
        }
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
    }

    private fun writeBirByRefStore(value: BirByRefStore, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.ptr == null) 0 else 1) + (if (value.local == null) 0 else 1))
        packer.packString("k")
        packer.packString("byrefStore")
        value.ptr?.let { child ->
            packer.packString("ptr")
            write(child, packer, depth + 1)
        }
        value.local?.let { child ->
            packer.packString("local")
            MessagePackText.write(packer, child)
        }
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("value")
        write(value.value, packer, depth + 1)
    }

    private fun writeBirReturnExpression(value: BirReturnExpression, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1 + (if (value.value == null) 0 else 1))
        packer.packString("k")
        packer.packString("returnExpr")
        value.value?.let { child ->
            packer.packString("value")
            write(child, packer, depth + 1)
        }
    }

    private fun writeBirUnsupportedExpression(value: BirUnsupportedExpression, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("unsupportedExpr")
        packer.packString("of")
        MessagePackText.write(packer, value.of)
    }

    private fun writeBirPropertyFunction(value: BirPropertyFunction, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1)
        packer.packString("iface")
        writeType(value.iface, packer, depth + 1)
    }

    private fun writeBirConstant(value: BirConstant, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("const")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("value")
        writeLiteral(value.value, packer, depth + 1)
    }

    private fun writeBirObjectEquals(value: BirObjectEquals, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("objEq")
        packer.packString("lhs")
        write(value.lhs, packer, depth + 1)
        packer.packString("rhs")
        write(value.rhs, packer, depth + 1)
    }

    private fun writeBirExpressionStatement(value: BirExpressionStatement, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("exprStmt")
        packer.packString("expr")
        write(value.expr, packer, depth + 1)
    }

    private fun writeBirThrowExpression(value: BirThrowExpression, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("throwExpr")
        packer.packString("value")
        write(value.value, packer, depth + 1)
    }

    private fun writeBirUnaryOperation(value: BirUnaryOperation, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("unaryOp")
        packer.packString("op")
        MessagePackText.write(packer, value.op)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirConditional(value: BirConditional, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.joinNullBranch == null) 0 else 1) + (if (value.type == null) 0 else 1))
        packer.packString("k")
        packer.packString("cond")
        value.joinNullBranch?.let { child ->
            packer.packString("joinNullBranch")
            packer.packBoolean(child)
        }
        value.type?.let { child ->
            packer.packString("type")
            writeType(child, packer, depth + 1)
        }
        packer.packString("cond")
        write(value.cond, packer, depth + 1)
        packer.packString("then")
        write(value.thenValue, packer, depth + 1)
        packer.packString("else")
        write(value.elseValue, packer, depth + 1)
    }

    private fun writeBirNullableHasValue(value: BirNullableHasValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("nullableHasValue")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirNullableValue(value: BirNullableValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("nullableValue")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("e")
        write(value.e, packer, depth + 1)
    }

    private fun writeBirEnumValue(value: BirEnumValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("enumValue")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("entry")
        MessagePackText.write(packer, value.entry)
        packer.packString("ordinal")
        packer.packInt(value.ordinal)
    }

    private fun writeBirEnumValues(value: BirEnumValues, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("enumValues")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
    }

    private fun writeBirCompanionValue(value: BirCompanionValue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("companionValue")
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("companionType")
        writeType(value.companionType, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
    }

    private fun writeBirClassReference(value: BirClassReference, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("classRef")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
    }

    private fun writeBirNullableNull(value: BirNullableNull, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("nullableNull")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
    }

    private fun writeBirDefaultParameter(value: BirDefaultParameter, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("defaultArgParam")
        packer.packString("idx")
        packer.packInt(value.idx)
    }

    private fun writeBirDefaultReceiver(value: BirDefaultReceiver, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("defaultArgReceiver")
        packer.packString("kind")
        MessagePackText.write(packer, value.kind)
    }

    private fun writeBirBindingReference(value: BirBindingReference, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("bindRef")
        packer.packString("id")
        MessagePackText.write(packer, value.id)
        packer.packString("sty")
        writeType(value.sty, packer, depth + 1)
    }

    private fun writeBirLocalFunctionReference(value: BirLocalFunctionReference, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.typeArgs == null) 0 else 1))
        packer.packString("k")
        packer.packString("localFunRef")
        packer.packString("id")
        MessagePackText.write(packer, value.id)
        packer.packString("funcType")
        writeType(value.funcType, packer, depth + 1)
        value.typeArgs?.let { child ->
            packer.packString("typeArgs")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirValueBlock(value: BirValueBlock, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.joinNullBranch == null) 0 else 1) + (if (value.type == null) 0 else 1))
        packer.packString("k")
        packer.packString("valueBlock")
        value.joinNullBranch?.let { child ->
            packer.packString("joinNullBranch")
            packer.packBoolean(child)
        }
        value.type?.let { child ->
            packer.packString("type")
            writeType(child, packer, depth + 1)
        }
        packer.packString("stmts")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.stmts.size)
        for (item0 in value.stmts) {
            write(item0, packer, depth + 1 + 1)
        }
        packer.packString("result")
        write(value.result, packer, depth + 1)
    }

    private fun writeBirNew(value: BirNew, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.argTypes == null) 0 else 1) + (if (value.constructorDeclarationId == null) 0 else 1) + (if (value.companionCaptureOwner == null) 0 else 1) + (if (value.propertyFunction == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberMethodTypeParams == null) 0 else 1) + (if (value.memberSignature == null) 0 else 1) + (if (value.memberReturnType == null) 0 else 1))
        packer.packString("k")
        packer.packString("new")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        value.argTypes?.let { child ->
            packer.packString("argTypes")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        packer.packString("args")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.args.size)
        for (item0 in value.args) {
            write(item0, packer, depth + 1 + 1)
        }
        value.constructorDeclarationId?.let { child ->
            packer.packString("constructorDeclarationId")
            MessagePackText.write(packer, child)
        }
        value.companionCaptureOwner?.let { child ->
            packer.packString("companionCaptureOwner")
            MessagePackText.write(packer, child)
        }
        value.propertyFunction?.let { child ->
            packer.packString("propertyFunction")
            writeBirPropertyFunction(child, packer, depth + 1)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberMethodTypeParams?.let { child ->
            packer.packString("memberMethodTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberSignature?.let { child ->
            packer.packString("memberSignature")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.memberReturnType?.let { child ->
            packer.packString("memberReturnType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirThrow(value: BirThrow, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("throw")
        packer.packString("value")
        write(value.value, packer, depth + 1)
    }

    private fun writeBirIf(value: BirIf, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("if")
        packer.packString("branches")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.branches.size)
        for (item0 in value.branches) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirReturn(value: BirReturn, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1 + (if (value.value == null) 0 else 1))
        packer.packString("k")
        packer.packString("return")
        value.value?.let { child ->
            packer.packString("value")
            write(child, packer, depth + 1)
        }
    }

    private fun writeBirSetLocal(value: BirSetLocal, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("k")
        packer.packString("setLocal")
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("value")
        write(value.value, packer, depth + 1)
    }

    private fun writeBirSetField(value: BirSetField, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5 + (if (value.outer == null) 0 else 1) + (if (value.memberVisibility == null) 0 else 1) + (if (value.memberOwnerTypeParams == null) 0 else 1) + (if (value.memberType == null) 0 else 1))
        packer.packString("k")
        packer.packString("setField")
        packer.packString("ownerType")
        writeType(value.ownerType, packer, depth + 1)
        packer.packString("recv")
        write(value.recv, packer, depth + 1)
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("value")
        write(value.value, packer, depth + 1)
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
        value.memberVisibility?.let { child ->
            packer.packString("memberVisibility")
            MessagePackText.write(packer, child)
        }
        value.memberOwnerTypeParams?.let { child ->
            packer.packString("memberOwnerTypeParams")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeTypeParameterDeclaration(item0, packer, depth + 1 + 1)
            }
        }
        value.memberType?.let { child ->
            packer.packString("memberType")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirBlock(value: BirBlock, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("block")
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirBranchIf(value: BirBranchIf, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4)
        packer.packString("k")
        packer.packString("brIf")
        packer.packString("id")
        packer.packInt(value.id)
        packer.packString("on")
        packer.packBoolean(value.on)
        packer.packString("cond")
        write(value.cond, packer, depth + 1)
    }

    private fun writeBirCatch(value: BirCatch, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("excType")
        writeType(value.excType, packer, depth + 1)
        packer.packString("var")
        MessagePackText.write(packer, value.variable)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirTry(value: BirTry, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.finallyBody == null) 0 else 1))
        packer.packString("k")
        packer.packString("try")
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
        packer.packString("catches")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.catches.size)
        for (item0 in value.catches) {
            writeBirCatch(item0, packer, depth + 1 + 1)
        }
        value.finallyBody?.let { child ->
            packer.packString("finally")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                write(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeBirForArray(value: BirForArray, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5)
        packer.packString("k")
        packer.packString("forArray")
        packer.packString("label")
        if (value.label == null) packer.packNil()
        else {
            MessagePackText.write(packer, value.label)
        }
        packer.packString("var")
        MessagePackText.write(packer, value.variable)
        packer.packString("array")
        write(value.array, packer, depth + 1)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirForIn(value: BirForIn, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(8)
        packer.packString("k")
        packer.packString("forIn")
        packer.packString("label")
        if (value.label == null) packer.packNil()
        else {
            MessagePackText.write(packer, value.label)
        }
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
        packer.packString("src")
        write(value.src, packer, depth + 1)
        packer.packString("srcType")
        writeType(value.srcType, packer, depth + 1)
        packer.packString("var")
        MessagePackText.write(packer, value.variable)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
        packer.packString("fallback")
        writeBirBlock(value.fallback, packer, depth + 1)
    }

    private fun writeBirConditionalBranch(value: BirConditionalBranch, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("cond")
        write(value.cond, packer, depth + 1)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirElseBranch(value: BirElseBranch, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("else")
        packer.packBoolean(true)
        packer.packString("body")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.body.size)
        for (item0 in value.body) {
            write(item0, packer, depth + 1 + 1)
        }
    }

    private fun writeBirVariable(value: BirVariable, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3 + (if (value.init == BirField.Absent) 0 else 1) + (if (value.joinNullBranch == null) 0 else 1) + (if (value.outer == null) 0 else 1))
        packer.packString("k")
        packer.packString("var")
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        packer.packString("type")
        writeType(value.type, packer, depth + 1)
        when (val present = value.init) {
            BirField.Absent -> {}
            BirField.Null -> {
                packer.packString("init")
                packer.packNil()
            }
            is BirField.Value -> {
                packer.packString("init")
                write(present.value, packer, depth + 1)
            }
        }
        value.joinNullBranch?.let { child ->
            packer.packString("joinNullBranch")
            packer.packBoolean(child)
        }
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
    }

    private fun writeBirLocal(value: BirLocal, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.outer == null) 0 else 1) + (if (value.sty == null) 0 else 1))
        packer.packString("k")
        packer.packString("local")
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        value.outer?.let { child ->
            packer.packString("outer")
            packer.packBoolean(child)
        }
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirThis(value: BirThis, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1 + (if (value.sty == null) 0 else 1))
        packer.packString("k")
        packer.packString("this")
        value.sty?.let { child ->
            packer.packString("sty")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeBirSourcePoint(value: BirSourcePoint, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1 + (if (value.span == null) 0 else 1))
        packer.packString("k")
        packer.packString("sourcePoint")
        value.span?.let { child ->
            packer.packString("span")
            writeBirSourceSpan(child, packer, depth + 1)
        }
    }

    private fun writeBirBreak(value: BirBreak, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("break")
        packer.packString("label")
        if (value.label == null) packer.packNil()
        else {
            MessagePackText.write(packer, value.label)
        }
    }

    private fun writeBirContinue(value: BirContinue, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("continue")
        packer.packString("label")
        if (value.label == null) packer.packNil()
        else {
            MessagePackText.write(packer, value.label)
        }
    }

    private fun writeBirGoto(value: BirGoto, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("goto")
        packer.packString("id")
        packer.packInt(value.id)
    }

    private fun writeBirLabel(value: BirLabel, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("k")
        packer.packString("label")
        packer.packString("id")
        packer.packInt(value.id)
    }

    private fun writeBirSourceSpan(value: BirSourceSpan, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(5)
        packer.packString("f")
        MessagePackText.write(packer, value.f)
        packer.packString("l")
        packer.packInt(value.l)
        packer.packString("c")
        packer.packInt(value.c)
        packer.packString("el")
        packer.packInt(value.el)
        packer.packString("ec")
        packer.packInt(value.ec)
    }

    private fun writeTypeParameterDeclaration(value: TypeParameterDeclaration, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1 + (if (value.reified == null) 0 else 1) + (if (value.variance == null) 0 else 1) + (if (value.constraints == null) 0 else 1))
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        value.reified?.let { child ->
            packer.packString("reified")
            packer.packBoolean(child)
        }
        value.variance?.let { child ->
            packer.packString("variance")
            MessagePackText.write(packer, child)
        }
        value.constraints?.let { child ->
            packer.packString("constraints")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
    }

    private fun writeType(value: TypeNode, packer: MessagePacker, depth: Int) {
        when (value) {
            is TypeNode.Fqn -> writeTypeFqn(value, packer, depth)
            is TypeNode.Tv -> writeTypeTv(value, packer, depth)
            is TypeNode.Star -> writeTypeStar(value, packer, depth)
            is TypeNode.Projection -> writeTypeProjection(value, packer, depth)
            is TypeNode.Fn -> writeTypeFn(value, packer, depth)
            is TypeNode.Nullable -> writeTypeNullable(value, packer, depth)
            is TypeNode.Oblivious -> writeTypeOblivious(value, packer, depth)
            is TypeNode.Array -> writeTypeArray(value, packer, depth)
            is TypeNode.ByRef -> writeTypeByRef(value, packer, depth)
        }
    }

    private fun writeTypeFqn(value: TypeNode.Fqn, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2 + (if (value.args == null) 0 else 1) + (if (value.classifier == null) 0 else 1))
        packer.packString("t")
        packer.packString("fqn")
        packer.packString("name")
        MessagePackText.write(packer, value.name)
        value.args?.let { child ->
            packer.packString("args")
            checkDepth(depth + 1)
            packer.packArrayHeader(child.size)
            for (item0 in child) {
                writeType(item0, packer, depth + 1 + 1)
            }
        }
        value.classifier?.let { child ->
            packer.packString("classifier")
            writeTypeSourceClassifier(child, packer, depth + 1)
        }
    }

    private fun writeTypeTv(value: TypeNode.Tv, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("t")
        packer.packString("tv")
        packer.packString("scope")
        MessagePackText.write(packer, value.scope)
        packer.packString("i")
        packer.packInt(value.i)
    }

    private fun writeTypeStar(value: TypeNode.Star, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(1)
        packer.packString("t")
        packer.packString("star")
    }

    private fun writeTypeProjection(value: TypeNode.Projection, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("t")
        packer.packString("projection")
        packer.packString("variance")
        MessagePackText.write(packer, value.variance)
        packer.packString("of")
        writeType(value.of, packer, depth + 1)
    }

    private fun writeTypeFn(value: TypeNode.Fn, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(4 + (if (value.recv == null) 0 else 1))
        packer.packString("t")
        packer.packString("fn")
        packer.packString("suspend")
        packer.packBoolean(value.suspend)
        packer.packString("ret")
        writeType(value.ret, packer, depth + 1)
        packer.packString("params")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.params.size)
        for (item0 in value.params) {
            writeType(item0, packer, depth + 1 + 1)
        }
        value.recv?.let { child ->
            packer.packString("recv")
            writeType(child, packer, depth + 1)
        }
    }

    private fun writeTypeNullable(value: TypeNode.Nullable, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("t")
        packer.packString("nullable")
        packer.packString("of")
        writeType(value.of, packer, depth + 1)
    }

    private fun writeTypeOblivious(value: TypeNode.Oblivious, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("t")
        packer.packString("oblivious")
        packer.packString("of")
        writeType(value.of, packer, depth + 1)
    }

    private fun writeTypeArray(value: TypeNode.Array, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("t")
        packer.packString("array")
        packer.packString("elem")
        writeType(value.elem, packer, depth + 1)
    }

    private fun writeTypeByRef(value: TypeNode.ByRef, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(2)
        packer.packString("t")
        packer.packString("byRef")
        packer.packString("of")
        writeType(value.of, packer, depth + 1)
    }

    private fun writeTypeSourceClassifier(value: TypeNode.SourceClassifier, packer: MessagePacker, depth: Int) {
        checkDepth(depth)
        packer.packMapHeader(3)
        packer.packString("packageName")
        MessagePackText.write(packer, value.packageName)
        packer.packString("ownerPath")
        checkDepth(depth + 1)
        packer.packArrayHeader(value.ownerPath.size)
        for (item0 in value.ownerPath) {
            MessagePackText.write(packer, item0)
        }
        packer.packString("name")
        MessagePackText.write(packer, value.name)
    }

    private fun writeLiteral(value: BirLiteral, packer: MessagePacker, depth: Int) {
        when (value) {
            BirLiteral.NullLiteral -> packer.packNil()
            is BirLiteral.BooleanLiteral -> packer.packBoolean(value.value)
            is BirLiteral.IntegerLiteral -> packer.packLong(value.value)
            is BirLiteral.TextLiteral -> MessagePackText.write(packer, value.value)
            is Float32Literal -> {
                checkDepth(depth)
                packer.packMapHeader(2)
                packer.packString("encoding").packString("f32")
                packer.packString("bits").packInt(value.bits)
            }
            is Float64Literal -> {
                checkDepth(depth)
                packer.packMapHeader(2)
                packer.packString("encoding").packString("f64")
                packer.packString("bits").packLong(value.bits)
            }
        }
    }
}
