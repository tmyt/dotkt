// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

data class BirBinaryOperation(
    val op: String,
    val lhs: BirExpression,
    val rhs: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"binOp\"")
        output.append(",\"op\":")
        TypeNodeJson.writeString(op, output)
        output.append(",\"lhs\":")
        lhs.writeJson(output)
        output.append(",\"rhs\":")
        rhs.writeJson(output)
        output.append('}')
    }
}

data class BirCast(
    val type: TypeNode,
    val e: BirExpression,
    val reifiedTypeOperand: Boolean? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"cast\"")
        output.append(",\"type\":")
        type.writeJson(output)
        reifiedTypeOperand?.let { value ->
            output.append(",\"reifiedTypeOperand\":")
            output.append(value)
        }
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirIsInstance(
    val type: TypeNode,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"isInst\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirInstanceOrNull(
    val type: TypeNode,
    val e: BirExpression,
    val reifiedTypeOperand: Boolean? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"isInstRef\"")
        output.append(",\"type\":")
        type.writeJson(output)
        reifiedTypeOperand?.let { value ->
            output.append(",\"reifiedTypeOperand\":")
            output.append(value)
        }
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirSafeValueCast(
    val elem: TypeNode,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"safeCastValue\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirConcat(
    val parts: List<BirExpression>
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"concat\"")
        output.append(",\"parts\":")
        output.append('[')
        parts.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirRuntimeClass(
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"getType\"")
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirNullableWrap(
    val elem: TypeNode,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"nullableWrap\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirArrayGet(
    val array: BirExpression,
    val index: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"arrayGet\"")
        output.append(",\"array\":")
        array.writeJson(output)
        output.append(",\"index\":")
        index.writeJson(output)
        output.append('}')
    }
}

data class BirArraySet(
    val array: BirExpression,
    val index: BirExpression,
    val value: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"arraySet\"")
        output.append(",\"array\":")
        array.writeJson(output)
        output.append(",\"index\":")
        index.writeJson(output)
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirArrayLength(
    val array: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"arrayLen\"")
        output.append(",\"array\":")
        array.writeJson(output)
        output.append('}')
    }
}

data class BirNewArray(
    val elem: TypeNode,
    val elems: List<BirExpression>
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newArray\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"elems\":")
        output.append('[')
        elems.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirNewArraySized(
    val elem: TypeNode,
    val size: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newArraySized\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"size\":")
        size.writeJson(output)
        output.append('}')
    }
}

data class BirNewArrayInit(
    val elem: TypeNode,
    val size: BirExpression,
    val init: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newArrayInit\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"size\":")
        size.writeJson(output)
        output.append(",\"init\":")
        init.writeJson(output)
        output.append('}')
    }
}

data class BirSpreadPart(
    val spread: Boolean,
    val e: BirExpression
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        output.append("\"spread\":")
        output.append(spread)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirSpreadConcat(
    val elem: TypeNode,
    val parts: List<BirSpreadPart>
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"spreadConcat\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"parts\":")
        output.append('[')
        parts.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirDefaultValue(
    val type: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"default\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append('}')
    }
}

data class BirEnumName(
    val type: TypeNode,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"enumName\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirEnumOrdinal(
    val e: BirExpression,
    val type: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"enumOrdinal\"")
        output.append(",\"e\":")
        e.writeJson(output)
        output.append(",\"type\":")
        type.writeJson(output)
        output.append('}')
    }
}

data class BirEnumParse(
    val type: TypeNode,
    val arg: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"enumParse\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"arg\":")
        arg.writeJson(output)
        output.append('}')
    }
}

data class BirObjectMethod(
    val method: String,
    val recv: BirExpression,
    val arg: BirExpression? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"objMethod\"")
        output.append(",\"method\":")
        TypeNodeJson.writeString(method, output)
        output.append(",\"recv\":")
        recv.writeJson(output)
        arg?.let { value ->
            output.append(",\"arg\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirCallBinding(
    val id: String,
    val phase: String,
    val kind: String,
    val stable: Boolean,
    val type: TypeNode,
    val role: String,
    val expr: BirExpression
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        output.append("\"id\":")
        TypeNodeJson.writeString(id, output)
        output.append(",\"phase\":")
        TypeNodeJson.writeString(phase, output)
        output.append(",\"kind\":")
        TypeNodeJson.writeString(kind, output)
        output.append(",\"stable\":")
        output.append(stable)
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"role\":")
        TypeNodeJson.writeString(role, output)
        output.append(",\"expr\":")
        expr.writeJson(output)
        output.append('}')
    }
}

data class BirCallEvaluation(
    val type: TypeNode,
    val bindings: List<BirCallBinding>,
    val expr: BirExpression,
    val semanticOwner: String? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"callEval\"")
        output.append(",\"type\":")
        type.writeJson(output)
        semanticOwner?.let { value ->
            output.append(",\"semanticOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append(",\"bindings\":")
        output.append('[')
        bindings.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"expr\":")
        expr.writeJson(output)
        output.append('}')
    }
}

data object BirDefaultArgument : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"defaultArg\"")
        output.append('}')
    }
}

data class BirOverride(
    val owner: TypeNode,
    val member: String,
    val kind: String,
    val arity: Int,
    val declarationId: String? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        output.append("\"owner\":")
        owner.writeJson(output)
        output.append(",\"member\":")
        TypeNodeJson.writeString(member, output)
        output.append(",\"kind\":")
        TypeNodeJson.writeString(kind, output)
        output.append(",\"arity\":")
        output.append(arity)
        declarationId?.let { value ->
            output.append(",\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append('}')
    }
}

data class BirCallStatic(
    val method: String,
    val args: List<BirExpression>,
    val owner: BirField<TypeNode> = BirField.Absent,
    val ownerType: TypeNode? = null,
    val prop: String? = null,
    val sig: List<TypeNode>? = null,
    val typeArgs: List<TypeNode>? = null,
    val shapeTypes: List<TypeNode>? = null,
    val argTypes: List<TypeNode>? = null,
    val ret: TypeNode? = null,
    val suspendCall: Boolean? = null,
    val clrAwaitBridge: Boolean? = null,
    val awaitResult: TypeNode? = null,
    val calleeOwner: TypeNode? = null,
    val companionReceiver: TypeNode? = null,
    val declarationId: String? = null,
    val defaultDeclarationId: String? = null,
    val defaultOwnerType: TypeNode? = null,
    val dotktFrontendDeclarationConsumed: Boolean? = null,
    val clrFlagsOperation: String? = null,
    val anySlot: Boolean? = null,
    val sty: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberMethodTypeParams: List<TypeParameterDeclaration>? = null,
    val memberSignature: List<TypeNode>? = null,
    val memberReturnType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"callStatic\"")
        when (val present = owner) {
            BirField.Absent -> {}
            BirField.Null -> {
                output.append(",\"owner\":")
                output.append("null")
            }
            is BirField.Value -> {
                output.append(",\"owner\":")
                present.value.writeJson(output)
            }
        }
        ownerType?.let { value ->
            output.append(",\"ownerType\":")
            value.writeJson(output)
        }
        output.append(",\"method\":")
        TypeNodeJson.writeString(method, output)
        prop?.let { value ->
            output.append(",\"prop\":")
            TypeNodeJson.writeString(value, output)
        }
        sig?.let { value ->
            output.append(",\"sig\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        typeArgs?.let { value ->
            output.append(",\"typeArgs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        shapeTypes?.let { value ->
            output.append(",\"shapeTypes\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        argTypes?.let { value ->
            output.append(",\"argTypes\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        ret?.let { value ->
            output.append(",\"ret\":")
            value.writeJson(output)
        }
        output.append(",\"args\":")
        output.append('[')
        args.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        suspendCall?.let { value ->
            output.append(",\"suspendCall\":")
            output.append(value)
        }
        clrAwaitBridge?.let { value ->
            output.append(",\"clrAwaitBridge\":")
            output.append(value)
        }
        awaitResult?.let { value ->
            output.append(",\"awaitResult\":")
            value.writeJson(output)
        }
        calleeOwner?.let { value ->
            output.append(",\"calleeOwner\":")
            value.writeJson(output)
        }
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        declarationId?.let { value ->
            output.append(",\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        defaultDeclarationId?.let { value ->
            output.append(",\"defaultDeclarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        defaultOwnerType?.let { value ->
            output.append(",\"defaultOwnerType\":")
            value.writeJson(output)
        }
        dotktFrontendDeclarationConsumed?.let { value ->
            output.append(",\"dotktFrontendDeclarationConsumed\":")
            output.append(value)
        }
        clrFlagsOperation?.let { value ->
            output.append(",\"clrFlagsOperation\":")
            TypeNodeJson.writeString(value, output)
        }
        anySlot?.let { value ->
            output.append(",\"anySlot\":")
            output.append(value)
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberMethodTypeParams?.let { value ->
            output.append(",\"memberMethodTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberSignature?.let { value ->
            output.append(",\"memberSignature\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberReturnType?.let { value ->
            output.append(",\"memberReturnType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirCallInstance(
    val recv: BirExpression,
    val method: String,
    val args: List<BirExpression>,
    val ownerType: TypeNode? = null,
    val virtual: Boolean? = null,
    val prop: String? = null,
    val sig: List<TypeNode>? = null,
    val typeArgs: List<TypeNode>? = null,
    val shapeTypes: List<TypeNode>? = null,
    val argTypes: List<TypeNode>? = null,
    val dynRet: TypeNode? = null,
    val ret: TypeNode? = null,
    val suspendCall: Boolean? = null,
    val clrAwaitBridge: Boolean? = null,
    val awaitResult: TypeNode? = null,
    val overrides: List<BirOverride>? = null,
    val anySlot: Boolean? = null,
    val superCall: Boolean? = null,
    val companionReceiver: TypeNode? = null,
    val declarationId: String? = null,
    val defaultDeclarationId: String? = null,
    val defaultOwnerType: TypeNode? = null,
    val dotktFrontendDeclarationConsumed: Boolean? = null,
    val clrFlagsOperation: String? = null,
    val sty: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberMethodTypeParams: List<TypeParameterDeclaration>? = null,
    val memberSignature: List<TypeNode>? = null,
    val memberReturnType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"callInstance\"")
        ownerType?.let { value ->
            output.append(",\"ownerType\":")
            value.writeJson(output)
        }
        virtual?.let { value ->
            output.append(",\"virtual\":")
            output.append(value)
        }
        output.append(",\"recv\":")
        recv.writeJson(output)
        output.append(",\"method\":")
        TypeNodeJson.writeString(method, output)
        prop?.let { value ->
            output.append(",\"prop\":")
            TypeNodeJson.writeString(value, output)
        }
        sig?.let { value ->
            output.append(",\"sig\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        typeArgs?.let { value ->
            output.append(",\"typeArgs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        shapeTypes?.let { value ->
            output.append(",\"shapeTypes\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        argTypes?.let { value ->
            output.append(",\"argTypes\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        dynRet?.let { value ->
            output.append(",\"dynRet\":")
            value.writeJson(output)
        }
        ret?.let { value ->
            output.append(",\"ret\":")
            value.writeJson(output)
        }
        output.append(",\"args\":")
        output.append('[')
        args.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        suspendCall?.let { value ->
            output.append(",\"suspendCall\":")
            output.append(value)
        }
        clrAwaitBridge?.let { value ->
            output.append(",\"clrAwaitBridge\":")
            output.append(value)
        }
        awaitResult?.let { value ->
            output.append(",\"awaitResult\":")
            value.writeJson(output)
        }
        overrides?.let { value ->
            output.append(",\"overrides\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        anySlot?.let { value ->
            output.append(",\"anySlot\":")
            output.append(value)
        }
        superCall?.let { value ->
            output.append(",\"super\":")
            output.append(value)
        }
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        declarationId?.let { value ->
            output.append(",\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        defaultDeclarationId?.let { value ->
            output.append(",\"defaultDeclarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        defaultOwnerType?.let { value ->
            output.append(",\"defaultOwnerType\":")
            value.writeJson(output)
        }
        dotktFrontendDeclarationConsumed?.let { value ->
            output.append(",\"dotktFrontendDeclarationConsumed\":")
            output.append(value)
        }
        clrFlagsOperation?.let { value ->
            output.append(",\"clrFlagsOperation\":")
            TypeNodeJson.writeString(value, output)
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberMethodTypeParams?.let { value ->
            output.append(",\"memberMethodTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberSignature?.let { value ->
            output.append(",\"memberSignature\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberReturnType?.let { value ->
            output.append(",\"memberReturnType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirCallLocal(
    val id: String,
    val args: List<BirExpression>,
    val sig: List<TypeNode>? = null,
    val typeArgs: List<TypeNode>? = null,
    val ret: TypeNode? = null,
    val sty: TypeNode? = null,
    val suspendCall: Boolean? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"callLocal\"")
        output.append(",\"id\":")
        TypeNodeJson.writeString(id, output)
        sig?.let { value ->
            output.append(",\"sig\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        output.append(",\"args\":")
        output.append('[')
        args.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        typeArgs?.let { value ->
            output.append(",\"typeArgs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        ret?.let { value ->
            output.append(",\"ret\":")
            value.writeJson(output)
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        suspendCall?.let { value ->
            output.append(",\"suspendCall\":")
            output.append(value)
        }
        output.append('}')
    }
}

data class BirFieldGet(
    val ownerType: TypeNode,
    val recv: BirExpression,
    val name: String,
    val outer: Boolean? = null,
    val dataClassCopyDefault: Boolean? = null,
    val generatedEqualsFieldRead: Boolean? = null,
    val ret: TypeNode? = null,
    val sty: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"field\"")
        output.append(",\"ownerType\":")
        ownerType.writeJson(output)
        output.append(",\"recv\":")
        recv.writeJson(output)
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        outer?.let { value ->
            output.append(",\"outer\":")
            output.append(value)
        }
        dataClassCopyDefault?.let { value ->
            output.append(",\"dataClassCopyDefault\":")
            output.append(value)
        }
        generatedEqualsFieldRead?.let { value ->
            output.append(",\"generatedEqualsFieldRead\":")
            output.append(value)
        }
        ret?.let { value ->
            output.append(",\"ret\":")
            value.writeJson(output)
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberType?.let { value ->
            output.append(",\"memberType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirStaticFieldGet(
    val ownerType: TypeNode,
    val name: String,
    val companionReceiver: TypeNode? = null,
    val sty: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"staticField\"")
        output.append(",\"ownerType\":")
        ownerType.writeJson(output)
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberType?.let { value ->
            output.append(",\"memberType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirFieldSetExpression(
    val ownerType: TypeNode,
    val recv: BirExpression,
    val name: String,
    val value: BirExpression,
    val outer: Boolean? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"setFieldExpr\"")
        output.append(",\"ownerType\":")
        ownerType.writeJson(output)
        output.append(",\"recv\":")
        recv.writeJson(output)
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        output.append(",\"value\":")
        value.writeJson(output)
        outer?.let { value ->
            output.append(",\"outer\":")
            output.append(value)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberType?.let { value ->
            output.append(",\"memberType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirStaticFieldSet(
    val ownerType: TypeNode,
    val name: String,
    val value: BirExpression,
    val companionReceiver: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"staticFieldSet\"")
        output.append(",\"ownerType\":")
        ownerType.writeJson(output)
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        output.append(",\"value\":")
        value.writeJson(output)
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberType?.let { value ->
            output.append(",\"memberType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirLateinitGet(
    val ownerType: TypeNode,
    val name: String,
    val static: Boolean? = null,
    val recv: BirExpression? = null,
    val companionReceiver: TypeNode? = null,
    val sty: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"lateinitGet\"")
        output.append(",\"ownerType\":")
        ownerType.writeJson(output)
        static?.let { value ->
            output.append(",\"static\":")
            output.append(value)
        }
        recv?.let { value ->
            output.append(",\"recv\":")
            value.writeJson(output)
        }
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberType?.let { value ->
            output.append(",\"memberType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirLateinitIsInitialized(
    val lateinitDeclarationId: String,
    val ownerType: TypeNode? = null,
    val recv: BirExpression? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"lateinitIsInitialized\"")
        output.append(",\"lateinitDeclarationId\":")
        TypeNodeJson.writeString(lateinitDeclarationId, output)
        ownerType?.let { value ->
            output.append(",\"ownerType\":")
            value.writeJson(output)
        }
        recv?.let { value ->
            output.append(",\"recv\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirInlineReceivers(
    val extension: BirExpression? = null,
    val dispatch: BirExpression? = null,
    val dispatchTypeArgs: List<TypeNode>? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        extension?.let { value ->
            output.append("\"extension\":")
            value.writeJson(output)
        }
        dispatch?.let { value ->
            output.append(",\"dispatch\":")
            value.writeJson(output)
        }
        dispatchTypeArgs?.let { value ->
            output.append(",\"dispatchTypeArgs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        output.append('}')
    }
}

data class BirCallInline(
    val callee: TypeNode,
    val owner: TypeNode?,
    val pc: Int,
    val ga: Int,
    val typeArgs: List<TypeNode>,
    val recvs: BirInlineReceivers,
    val args: List<BirExpression?>,
    val retType: TypeNode,
    val paramSig: List<TypeNode>,
    val semanticOwner: String? = null,
    val companionReceiver: TypeNode? = null,
    val declarationId: String? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"callInline\"")
        output.append(",\"callee\":")
        callee.writeJson(output)
        output.append(",\"owner\":")
        if (owner == null) output.append("null")
        else {
            owner.writeJson(output)
        }
        output.append(",\"pc\":")
        output.append(pc)
        output.append(",\"ga\":")
        output.append(ga)
        output.append(",\"typeArgs\":")
        output.append('[')
        typeArgs.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"recvs\":")
        recvs.writeJson(output)
        output.append(",\"args\":")
        output.append('[')
        args.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            if (item0 == null) output.append("null")
            else {
                item0.writeJson(output)
            }
        }
        output.append(']')
        output.append(",\"retType\":")
        retType.writeJson(output)
        output.append(",\"paramSig\":")
        output.append('[')
        paramSig.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        semanticOwner?.let { value ->
            output.append(",\"semanticOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        declarationId?.let { value ->
            output.append(",\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append('}')
    }
}

data class BirNewBoundDelegate(
    val ownerType: TypeNode,
    val method: String,
    val recv: BirExpression,
    val funcType: TypeNode,
    val typeArgs: List<TypeNode>? = null,
    val argTypes: List<TypeNode>? = null,
    val virtual: Boolean? = null,
    val anySlot: Boolean? = null,
    val calleeOwner: TypeNode? = null,
    val companionReceiver: TypeNode? = null,
    val declarationId: String? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberMethodTypeParams: List<TypeParameterDeclaration>? = null,
    val memberSignature: List<TypeNode>? = null,
    val memberReturnType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newBoundDelegate\"")
        output.append(",\"ownerType\":")
        ownerType.writeJson(output)
        output.append(",\"method\":")
        TypeNodeJson.writeString(method, output)
        typeArgs?.let { value ->
            output.append(",\"typeArgs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        argTypes?.let { value ->
            output.append(",\"argTypes\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        virtual?.let { value ->
            output.append(",\"virtual\":")
            output.append(value)
        }
        output.append(",\"recv\":")
        recv.writeJson(output)
        output.append(",\"funcType\":")
        funcType.writeJson(output)
        anySlot?.let { value ->
            output.append(",\"anySlot\":")
            output.append(value)
        }
        calleeOwner?.let { value ->
            output.append(",\"calleeOwner\":")
            value.writeJson(output)
        }
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        declarationId?.let { value ->
            output.append(",\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberMethodTypeParams?.let { value ->
            output.append(",\"memberMethodTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberSignature?.let { value ->
            output.append(",\"memberSignature\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberReturnType?.let { value ->
            output.append(",\"memberReturnType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirByRefOf(
    val inner: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"byrefOf\"")
        output.append(",\"inner\":")
        inner.writeJson(output)
        output.append('}')
    }
}

data class BirByRefLoad(
    val elem: TypeNode,
    val ptr: BirExpression? = null,
    val local: String? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"byrefLoad\"")
        ptr?.let { value ->
            output.append(",\"ptr\":")
            value.writeJson(output)
        }
        local?.let { value ->
            output.append(",\"local\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append('}')
    }
}

data class BirByRefStore(
    val elem: TypeNode,
    val value: BirExpression,
    val ptr: BirExpression? = null,
    val local: String? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"byrefStore\"")
        ptr?.let { value ->
            output.append(",\"ptr\":")
            value.writeJson(output)
        }
        local?.let { value ->
            output.append(",\"local\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirReturnExpression(
    val value: BirExpression? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"returnExpr\"")
        value?.let { value ->
            output.append(",\"value\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirUnsupportedExpression(
    val of: String
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"unsupportedExpr\"")
        output.append(",\"of\":")
        TypeNodeJson.writeString(of, output)
        output.append('}')
    }
}

data class BirPropertyFunction(
    val iface: TypeNode
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        output.append("\"iface\":")
        iface.writeJson(output)
        output.append('}')
    }
}

data class BirConstant(
    val type: TypeNode,
    val value: BirLiteral
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"const\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirObjectEquals(
    val lhs: BirExpression,
    val rhs: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"objEq\"")
        output.append(",\"lhs\":")
        lhs.writeJson(output)
        output.append(",\"rhs\":")
        rhs.writeJson(output)
        output.append('}')
    }
}

data class BirExpressionStatement(
    val expr: BirExpression
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"exprStmt\"")
        output.append(",\"expr\":")
        expr.writeJson(output)
        output.append('}')
    }
}

data class BirThrowExpression(
    val value: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"throwExpr\"")
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirUnaryOperation(
    val op: String,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"unaryOp\"")
        output.append(",\"op\":")
        TypeNodeJson.writeString(op, output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirConditional(
    val cond: BirExpression,
    val thenValue: BirExpression,
    val elseValue: BirExpression,
    val joinNullBranch: Boolean? = null,
    val type: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"cond\"")
        joinNullBranch?.let { value ->
            output.append(",\"joinNullBranch\":")
            output.append(value)
        }
        type?.let { value ->
            output.append(",\"type\":")
            value.writeJson(output)
        }
        output.append(",\"cond\":")
        cond.writeJson(output)
        output.append(",\"then\":")
        thenValue.writeJson(output)
        output.append(",\"else\":")
        elseValue.writeJson(output)
        output.append('}')
    }
}

data class BirNullableHasValue(
    val elem: TypeNode,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"nullableHasValue\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirNullableValue(
    val elem: TypeNode,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"nullableValue\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirEnumValue(
    val type: TypeNode,
    val entry: String,
    val ordinal: Int
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"enumValue\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"entry\":")
        TypeNodeJson.writeString(entry, output)
        output.append(",\"ordinal\":")
        output.append(ordinal)
        output.append('}')
    }
}

data class BirEnumValues(
    val type: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"enumValues\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append('}')
    }
}

data class BirCompanionValue(
    val ownerType: TypeNode,
    val companionType: TypeNode,
    val name: String
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"companionValue\"")
        output.append(",\"ownerType\":")
        ownerType.writeJson(output)
        output.append(",\"companionType\":")
        companionType.writeJson(output)
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        output.append('}')
    }
}

data class BirClassReference(
    val type: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"classRef\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append('}')
    }
}

data class BirNullableNull(
    val elem: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"nullableNull\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append('}')
    }
}

data class BirDefaultParameter(
    val idx: Int
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"defaultArgParam\"")
        output.append(",\"idx\":")
        output.append(idx)
        output.append('}')
    }
}

data class BirDefaultReceiver(
    val kind: String
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"defaultArgReceiver\"")
        output.append(",\"kind\":")
        TypeNodeJson.writeString(kind, output)
        output.append('}')
    }
}

data class BirBindingReference(
    val id: String,
    val sty: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"bindRef\"")
        output.append(",\"id\":")
        TypeNodeJson.writeString(id, output)
        output.append(",\"sty\":")
        sty.writeJson(output)
        output.append('}')
    }
}

data class BirLocalFunctionReference(
    val id: String,
    val funcType: TypeNode,
    val typeArgs: List<TypeNode>? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"localFunRef\"")
        output.append(",\"id\":")
        TypeNodeJson.writeString(id, output)
        output.append(",\"funcType\":")
        funcType.writeJson(output)
        typeArgs?.let { value ->
            output.append(",\"typeArgs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        output.append('}')
    }
}

data class BirValueBlock(
    val stmts: List<BirStatement>,
    val result: BirExpression,
    val joinNullBranch: Boolean? = null,
    val type: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"valueBlock\"")
        joinNullBranch?.let { value ->
            output.append(",\"joinNullBranch\":")
            output.append(value)
        }
        type?.let { value ->
            output.append(",\"type\":")
            value.writeJson(output)
        }
        output.append(",\"stmts\":")
        output.append('[')
        stmts.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"result\":")
        result.writeJson(output)
        output.append('}')
    }
}

data class BirNew(
    val type: TypeNode,
    val args: List<BirExpression>,
    val argTypes: List<TypeNode>? = null,
    val constructorDeclarationId: String? = null,
    val companionCaptureOwner: String? = null,
    val propertyFunction: BirPropertyFunction? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberMethodTypeParams: List<TypeParameterDeclaration>? = null,
    val memberSignature: List<TypeNode>? = null,
    val memberReturnType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"new\"")
        output.append(",\"type\":")
        type.writeJson(output)
        argTypes?.let { value ->
            output.append(",\"argTypes\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        output.append(",\"args\":")
        output.append('[')
        args.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        constructorDeclarationId?.let { value ->
            output.append(",\"constructorDeclarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        companionCaptureOwner?.let { value ->
            output.append(",\"companionCaptureOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        propertyFunction?.let { value ->
            output.append(",\"propertyFunction\":")
            value.writeJson(output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        memberOwnerTypeParams?.let { value ->
            output.append(",\"memberOwnerTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberMethodTypeParams?.let { value ->
            output.append(",\"memberMethodTypeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberSignature?.let { value ->
            output.append(",\"memberSignature\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        memberReturnType?.let { value ->
            output.append(",\"memberReturnType\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirThrow(
    val value: BirExpression
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"throw\"")
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirIf(
    val branches: List<BirBranch>
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"if\"")
        output.append(",\"branches\":")
        output.append('[')
        branches.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirConditionalBranch(
    val cond: BirExpression,
    val body: List<BirStatement>
) : BirBranch {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        output.append("\"cond\":")
        cond.writeJson(output)
        output.append(",\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirElseBranch(
    val body: List<BirStatement>
) : BirBranch {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        output.append("\"else\":")
        output.append("true")
        output.append(",\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirVariable(
    val name: String,
    val type: TypeNode,
    val init: BirField<BirExpression> = BirField.Absent,
    val joinNullBranch: Boolean? = null
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"var\"")
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        output.append(",\"type\":")
        type.writeJson(output)
        when (val present = init) {
            BirField.Absent -> {}
            BirField.Null -> {
                output.append(",\"init\":")
                output.append("null")
            }
            is BirField.Value -> {
                output.append(",\"init\":")
                present.value.writeJson(output)
            }
        }
        joinNullBranch?.let { value ->
            output.append(",\"joinNullBranch\":")
            output.append(value)
        }
        output.append('}')
    }
}

data class BirLocal(
    val name: String,
    val outer: Boolean? = null,
    val sty: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"local\"")
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        outer?.let { value ->
            output.append(",\"outer\":")
            output.append(value)
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirThis(
    val sty: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"this\"")
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirSourcePoint(
    val span: BirSourceSpan? = null
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"sourcePoint\"")
        span?.let { value ->
            output.append(",\"span\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirBreak(
    val label: String?
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"break\"")
        output.append(",\"label\":")
        if (label == null) output.append("null")
        else {
            TypeNodeJson.writeString(label, output)
        }
        output.append('}')
    }
}

data class BirContinue(
    val label: String?
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"continue\"")
        output.append(",\"label\":")
        if (label == null) output.append("null")
        else {
            TypeNodeJson.writeString(label, output)
        }
        output.append('}')
    }
}

data class BirGoto(
    val id: Int
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"goto\"")
        output.append(",\"id\":")
        output.append(id)
        output.append('}')
    }
}

data class BirLabel(
    val id: Int
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"label\"")
        output.append(",\"id\":")
        output.append(id)
        output.append('}')
    }
}

data class BirSourceSpan(
    val f: String,
    val l: Int,
    val c: Int,
    val el: Int,
    val ec: Int
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        output.append("\"f\":")
        TypeNodeJson.writeString(f, output)
        output.append(",\"l\":")
        output.append(l)
        output.append(",\"c\":")
        output.append(c)
        output.append(",\"el\":")
        output.append(el)
        output.append(",\"ec\":")
        output.append(ec)
        output.append('}')
    }
}
