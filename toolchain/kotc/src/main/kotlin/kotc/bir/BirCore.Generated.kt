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
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"spread\":")
        output.append(spread)
        if (hasField) output.append(','); hasField = true; output.append("\"e\":")
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
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"id\":")
        TypeNodeJson.writeString(id, output)
        if (hasField) output.append(','); hasField = true; output.append("\"phase\":")
        TypeNodeJson.writeString(phase, output)
        if (hasField) output.append(','); hasField = true; output.append("\"kind\":")
        TypeNodeJson.writeString(kind, output)
        if (hasField) output.append(','); hasField = true; output.append("\"stable\":")
        output.append(stable)
        if (hasField) output.append(','); hasField = true; output.append("\"type\":")
        type.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"role\":")
        TypeNodeJson.writeString(role, output)
        if (hasField) output.append(','); hasField = true; output.append("\"expr\":")
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
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"owner\":")
        owner.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"member\":")
        TypeNodeJson.writeString(member, output)
        if (hasField) output.append(','); hasField = true; output.append("\"kind\":")
        TypeNodeJson.writeString(kind, output)
        if (hasField) output.append(','); hasField = true; output.append("\"arity\":")
        output.append(arity)
        declarationId?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append('}')
    }
}

data class BirCallStatic(
    val method: String,
    val args: List<BirExpression>,
    val companionCall: Boolean? = null,
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
        companionCall?.let { value ->
            output.append(",\"companionCall\":")
            output.append(value)
        }
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
    val companionCall: Boolean? = null,
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
        companionCall?.let { value ->
            output.append(",\"companionCall\":")
            output.append(value)
        }
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
    val companionCall: Boolean? = null,
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
        companionCall?.let { value ->
            output.append(",\"companionCall\":")
            output.append(value)
        }
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
    val companionCall: Boolean? = null,
    val ret: TypeNode? = null,
    val companionReceiver: TypeNode? = null,
    val sty: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"staticField\"")
        companionCall?.let { value ->
            output.append(",\"companionCall\":")
            output.append(value)
        }
        ret?.let { value ->
            output.append(",\"ret\":")
            value.writeJson(output)
        }
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
    val companionCall: Boolean? = null,
    val outer: Boolean? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"setFieldExpr\"")
        companionCall?.let { value ->
            output.append(",\"companionCall\":")
            output.append(value)
        }
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
    val companionCall: Boolean? = null,
    val companionReceiver: TypeNode? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"staticFieldSet\"")
        companionCall?.let { value ->
            output.append(",\"companionCall\":")
            output.append(value)
        }
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

data class BirModifiers(
    val isInline: Boolean? = null,
    val isInfix: Boolean? = null,
    val isOperator: Boolean? = null,
    val isTailrec: Boolean? = null,
    val isExternal: Boolean? = null,
    val isExtension: Boolean? = null,
    val isOverride: Boolean? = null,
    val isAbstract: Boolean? = null,
    val isOpen: Boolean? = null,
    val isSuspend: Boolean? = null,
    val isInlineOnly: Boolean? = null,
    val isVararg: Boolean? = null,
    val isNoinline: Boolean? = null,
    val isCrossinline: Boolean? = null,
    val isContext: Boolean? = null,
    val isExtensionReceiver: Boolean? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        isInline?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"inline\":")
            output.append(value)
        }
        isInfix?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"infix\":")
            output.append(value)
        }
        isOperator?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"operator\":")
            output.append(value)
        }
        isTailrec?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"tailrec\":")
            output.append(value)
        }
        isExternal?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"external\":")
            output.append(value)
        }
        isExtension?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"ext\":")
            output.append(value)
        }
        isOverride?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"override\":")
            output.append(value)
        }
        isAbstract?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"abstract\":")
            output.append(value)
        }
        isOpen?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"open\":")
            output.append(value)
        }
        isSuspend?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"suspend\":")
            output.append(value)
        }
        isInlineOnly?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"inlineOnly\":")
            output.append(value)
        }
        isVararg?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"vararg\":")
            output.append(value)
        }
        isNoinline?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"noinline\":")
            output.append(value)
        }
        isCrossinline?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"crossinline\":")
            output.append(value)
        }
        isContext?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"context\":")
            output.append(value)
        }
        isExtensionReceiver?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"extensionReceiver\":")
            output.append(value)
        }
        output.append('}')
    }
}

data class BirDefaultBody(
    val index: Int,
    val body: BirExpression
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"index\":")
        output.append(index)
        if (hasField) output.append(','); hasField = true; output.append("\"body\":")
        body.writeJson(output)
        output.append('}')
    }
}

data class BirAttributeNamedArgument(
    val kind: String,
    val name: String,
    val type: TypeNode,
    val value: BirExpression
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"kind\":")
        TypeNodeJson.writeString(kind, output)
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"type\":")
        type.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirAttribute(
    val attr: TypeNode,
    val argTypes: List<TypeNode>,
    val args: List<BirExpression>,
    val attrClr: Boolean? = null,
    val namedArgs: List<BirAttributeNamedArgument>? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"attr\":")
        attr.writeJson(output)
        attrClr?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"attrClr\":")
            output.append(value)
        }
        if (hasField) output.append(','); hasField = true; output.append("\"argTypes\":")
        output.append('[')
        argTypes.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"args\":")
        output.append('[')
        args.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        namedArgs?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"namedArgs\":")
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

data class BirParameter(
    val name: String,
    val type: TypeNode,
    val outer: Boolean? = null,
    val mods: BirModifiers? = null,
    val defaultValue: BirExpression? = null,
    val defaultBir: BirDefaultBody? = null,
    val ctxFnType: Int? = null,
    val sharedCellType: TypeNode? = null,
    val attrs: List<BirAttribute>? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"type\":")
        type.writeJson(output)
        outer?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"outer\":")
            output.append(value)
        }
        mods?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"mods\":")
            value.writeJson(output)
        }
        defaultValue?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"default\":")
            value.writeJson(output)
        }
        defaultBir?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"defaultBir\":")
            value.writeJson(output)
        }
        ctxFnType?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"ctxFnType\":")
            output.append(value)
        }
        sharedCellType?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"sharedCellType\":")
            value.writeJson(output)
        }
        attrs?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"attrs\":")
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

data class BirDefaultCarrier(
    val expr: BirExpression,
    val lifted: List<BirFunctionDeclaration>
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"defaultCarrier\"")
        output.append(",\"expr\":")
        expr.writeJson(output)
        output.append(",\"lifted\":")
        output.append('[')
        lifted.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirLocalFunction(
    val id: String,
    val decl: BirFunctionDeclaration
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"localFun\"")
        output.append(",\"id\":")
        TypeNodeJson.writeString(id, output)
        output.append(",\"decl\":")
        decl.writeJson(output)
        output.append('}')
    }
}

data class BirInheritedImplementation(
    val owner: TypeNode,
    val member: String,
    val kind: String,
    val arity: Int,
    val typeParams: List<TypeParameterDeclaration>,
    val sig: List<TypeNode>? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"owner\":")
        owner.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"member\":")
        TypeNodeJson.writeString(member, output)
        if (hasField) output.append(','); hasField = true; output.append("\"kind\":")
        TypeNodeJson.writeString(kind, output)
        if (hasField) output.append(','); hasField = true; output.append("\"arity\":")
        output.append(arity)
        if (hasField) output.append(','); hasField = true; output.append("\"typeParams\":")
        output.append('[')
        typeParams.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        sig?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"sig\":")
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

data class BirDeclarationPosition(
    val f: String,
    val l: Int? = null,
    val c: Int? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"f\":")
        TypeNodeJson.writeString(f, output)
        l?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"l\":")
            output.append(value)
        }
        c?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"c\":")
            output.append(value)
        }
        output.append('}')
    }
}

data class BirFunctionDeclaration(
    val params: List<BirParameter>,
    val ret: TypeNode,
    val body: List<BirStatement>,
    val name: String? = null,
    val sourceName: String? = null,
    val isStatic: Boolean? = null,
    val isOverride: Boolean? = null,
    val isVirtual: Boolean? = null,
    val generated: Boolean? = null,
    val typeParams: List<TypeParameterDeclaration>? = null,
    val semanticOwner: String? = null,
    val declarationId: String? = null,
    val mods: BirModifiers? = null,
    val suspendRet: TypeNode? = null,
    val overrides: List<BirOverride>? = null,
    val explicitClrName: String? = null,
    val kotlinStatic: Boolean? = null,
    val isAbstract: Boolean? = null,
    val objectOverride: Boolean? = null,
    val vis: String? = null,
    val companionReceiver: TypeNode? = null,
    val companionMemberKind: String? = null,
    val companionSourceName: String? = null,
    val retCtxFnType: Int? = null,
    val attrs: List<BirAttribute>? = null,
    val pos: BirDeclarationPosition? = null,
    val propertyName: String? = null,
    val propertyAccessor: String? = null,
    val propertyAssociation: String? = null,
    val eventProperty: Boolean? = null,
    val fakeOverride: Boolean? = null,
    val inheritedImplementation: BirInheritedImplementation? = null,
    val syntheticTypeArgs: List<TypeNode>? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        name?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"name\":")
            TypeNodeJson.writeString(value, output)
        }
        sourceName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"sourceName\":")
            TypeNodeJson.writeString(value, output)
        }
        isStatic?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"static\":")
            output.append(value)
        }
        isOverride?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"override\":")
            output.append(value)
        }
        isVirtual?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"virtual\":")
            output.append(value)
        }
        generated?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"generated\":")
            output.append(value)
        }
        if (hasField) output.append(','); hasField = true; output.append("\"params\":")
        output.append('[')
        params.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"ret\":")
        ret.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        typeParams?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"typeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        semanticOwner?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"semanticOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        declarationId?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        mods?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"mods\":")
            value.writeJson(output)
        }
        suspendRet?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"suspendRet\":")
            value.writeJson(output)
        }
        overrides?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"overrides\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        explicitClrName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"explicitClrName\":")
            TypeNodeJson.writeString(value, output)
        }
        kotlinStatic?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"kotlinStatic\":")
            output.append(value)
        }
        isAbstract?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"abstract\":")
            output.append(value)
        }
        objectOverride?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"objectOverride\":")
            output.append(value)
        }
        vis?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"vis\":")
            TypeNodeJson.writeString(value, output)
        }
        companionReceiver?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        companionMemberKind?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionMemberKind\":")
            TypeNodeJson.writeString(value, output)
        }
        companionSourceName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionSourceName\":")
            TypeNodeJson.writeString(value, output)
        }
        retCtxFnType?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"retCtxFnType\":")
            output.append(value)
        }
        attrs?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"attrs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        pos?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"pos\":")
            value.writeJson(output)
        }
        propertyName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"propertyName\":")
            TypeNodeJson.writeString(value, output)
        }
        propertyAccessor?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"propertyAccessor\":")
            TypeNodeJson.writeString(value, output)
        }
        propertyAssociation?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"propertyAssociation\":")
            TypeNodeJson.writeString(value, output)
        }
        eventProperty?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"eventProperty\":")
            output.append(value)
        }
        fakeOverride?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"fakeOverride\":")
            output.append(value)
        }
        inheritedImplementation?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"inheritedImplementation\":")
            value.writeJson(output)
        }
        syntheticTypeArgs?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"_syntheticTypeArgs\":")
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

data class BirConstructor(
    val params: List<BirParameter>,
    val baseArgs: List<BirExpression>?,
    val body: List<BirStatement>,
    val delegationSig: List<TypeNode>? = null,
    val thisArgs: BirField<List<BirExpression>> = BirField.Absent,
    val delegationBindings: List<BirCallBinding>? = null,
    val delegationOuterSlot: Int? = null,
    val delegationDeclarationId: String? = null,
    val declarationId: String? = null,
    val declarationSourceName: String? = null,
    val vis: String? = null,
    val attrs: List<BirAttribute>? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"params\":")
        output.append('[')
        params.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"baseArgs\":")
        if (baseArgs == null) output.append("null")
        else {
            output.append('[')
            baseArgs.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        delegationSig?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"delegationSig\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        when (val present = thisArgs) {
            BirField.Absent -> {}
            BirField.Null -> {
                if (hasField) output.append(','); hasField = true; output.append("\"thisArgs\":")
                output.append("null")
            }
            is BirField.Value -> {
                if (hasField) output.append(','); hasField = true; output.append("\"thisArgs\":")
                output.append('[')
                present.value.forEachIndexed { index0, item0 ->
                    if (index0 > 0) output.append(',')
                    item0.writeJson(output)
                }
                output.append(']')
            }
        }
        delegationBindings?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"delegationBindings\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        delegationOuterSlot?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"delegationOuterSlot\":")
            output.append(value)
        }
        delegationDeclarationId?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"delegationDeclarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        declarationId?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"declarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        declarationSourceName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"declarationSourceName\":")
            TypeNodeJson.writeString(value, output)
        }
        vis?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"vis\":")
            TypeNodeJson.writeString(value, output)
        }
        attrs?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"attrs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        if (hasField) output.append(','); hasField = true; output.append("\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirFieldDeclaration(
    val name: String,
    val type: TypeNode,
    val isStatic: Boolean? = null,
    val init: BirField<BirExpression> = BirField.Absent,
    val vis: String? = null,
    val readOnly: Boolean? = null,
    val initOnly: Boolean? = null,
    val isConst: Boolean? = null,
    val kotlinStatic: Boolean? = null,
    val outer: Boolean? = null,
    val isLateinit: Boolean? = null,
    val lateinitDeclarationId: String? = null,
    val isVolatile: Boolean? = null,
    val attrs: List<BirAttribute>? = null,
    val companionReceiver: TypeNode? = null,
    val companionMemberKind: String? = null,
    val companionSourceName: String? = null,
    val companionPropertyMutable: Boolean? = null,
    val companionStorageReadOnly: Boolean? = null,
    val companionSetterVisibility: String? = null,
    val companionGetterExplicitClrName: String? = null,
    val companionSetterExplicitClrName: String? = null,
    val companionGetterDeclarationId: String? = null,
    val companionSetterDeclarationId: String? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"type\":")
        type.writeJson(output)
        isStatic?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"static\":")
            output.append(value)
        }
        when (val present = init) {
            BirField.Absent -> {}
            BirField.Null -> {
                if (hasField) output.append(','); hasField = true; output.append("\"init\":")
                output.append("null")
            }
            is BirField.Value -> {
                if (hasField) output.append(','); hasField = true; output.append("\"init\":")
                present.value.writeJson(output)
            }
        }
        vis?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"vis\":")
            TypeNodeJson.writeString(value, output)
        }
        readOnly?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"readOnly\":")
            output.append(value)
        }
        initOnly?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"initOnly\":")
            output.append(value)
        }
        isConst?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"const\":")
            output.append(value)
        }
        kotlinStatic?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"kotlinStatic\":")
            output.append(value)
        }
        outer?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"outer\":")
            output.append(value)
        }
        isLateinit?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"lateinit\":")
            output.append(value)
        }
        lateinitDeclarationId?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"lateinitDeclarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        isVolatile?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"volatile\":")
            output.append(value)
        }
        attrs?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"attrs\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        companionReceiver?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        companionMemberKind?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionMemberKind\":")
            TypeNodeJson.writeString(value, output)
        }
        companionSourceName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionSourceName\":")
            TypeNodeJson.writeString(value, output)
        }
        companionPropertyMutable?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionPropertyMutable\":")
            output.append(value)
        }
        companionStorageReadOnly?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionStorageReadOnly\":")
            output.append(value)
        }
        companionSetterVisibility?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionSetterVisibility\":")
            TypeNodeJson.writeString(value, output)
        }
        companionGetterExplicitClrName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionGetterExplicitClrName\":")
            TypeNodeJson.writeString(value, output)
        }
        companionSetterExplicitClrName?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionSetterExplicitClrName\":")
            TypeNodeJson.writeString(value, output)
        }
        companionGetterDeclarationId?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionGetterDeclarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        companionSetterDeclarationId?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionSetterDeclarationId\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append('}')
    }
}

data class BirRefTypeDeclaration(
    val name: String,
    val elem: TypeNode,
    val typeParams: List<TypeParameterDeclaration>
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"elem\":")
        elem.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"typeParams\":")
        output.append('[')
        typeParams.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirClassDefinition(
    val name: String,
    val base: TypeNode?,
    val interfaces: List<TypeNode>,
    val fields: List<BirFieldDeclaration>,
    val ctors: List<BirConstructor>,
    val methods: List<BirFunctionDeclaration>,
    val companionCaptureOwner: String? = null,
    val typeParams: List<TypeParameterDeclaration>? = null,
    val semanticOwner: String? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"kind\":")
        output.append("\"class\"")
        if (hasField) output.append(','); hasField = true; output.append("\"generated\":")
        output.append("true")
        if (hasField) output.append(','); hasField = true; output.append("\"base\":")
        if (base == null) output.append("null")
        else {
            base.writeJson(output)
        }
        companionCaptureOwner?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"companionCaptureOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        typeParams?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"typeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        semanticOwner?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"semanticOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        if (hasField) output.append(','); hasField = true; output.append("\"interfaces\":")
        output.append('[')
        interfaces.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"fields\":")
        output.append('[')
        fields.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"ctors\":")
        output.append('[')
        ctors.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"methods\":")
        output.append('[')
        methods.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirNewSam(
    val samType: TypeNode,
    val captures: List<BirExpression>,
    val synthClass: BirClassDefinition,
    val typeArgs: List<TypeNode>? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newSam\"")
        output.append(",\"samType\":")
        samType.writeJson(output)
        output.append(",\"captures\":")
        output.append('[')
        captures.forEachIndexed { index0, item0 ->
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
        output.append(",\"synthClass\":")
        synthClass.writeJson(output)
        output.append('}')
    }
}

data class BirSamConversion(
    val type: TypeNode,
    val funcType: TypeNode,
    val e: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"samConvert\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"funcType\":")
        funcType.writeJson(output)
        output.append(",\"e\":")
        e.writeJson(output)
        output.append('}')
    }
}

data class BirCaptureDescriptor(
    val name: String,
    val type: TypeNode,
    val outer: Boolean? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"type\":")
        type.writeJson(output)
        outer?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"outer\":")
            output.append(value)
        }
        output.append('}')
    }
}

data class BirClosureDefinition(
    val name: String,
    val fields: List<BirCaptureDescriptor>,
    val params: List<BirLambdaParameter>,
    val ret: TypeNode,
    val body: List<BirStatement>,
    val typeParams: List<TypeParameterDeclaration>? = null,
    val semanticOwner: String? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"fields\":")
        output.append('[')
        fields.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"params\":")
        output.append('[')
        params.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        if (hasField) output.append(','); hasField = true; output.append("\"ret\":")
        ret.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        typeParams?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"typeParams\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                item0.writeJson(output)
            }
            output.append(']')
        }
        semanticOwner?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"semanticOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append('}')
    }
}

data class BirNewClosure(
    val closureType: TypeNode,
    val captures: List<BirExpression>,
    val method: String,
    val funcType: TypeNode,
    val synthClass: BirClosureDefinition,
    val typeArgs: List<TypeNode>? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newClosure\"")
        output.append(",\"closureType\":")
        closureType.writeJson(output)
        output.append(",\"captures\":")
        output.append('[')
        captures.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"method\":")
        TypeNodeJson.writeString(method, output)
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
        output.append(",\"synthClass\":")
        synthClass.writeJson(output)
        output.append('}')
    }
}

data class BirNewDelegate(
    val method: String,
    val funcType: TypeNode,
    val sig: List<TypeNode>? = null,
    val typeArgs: List<TypeNode>? = null,
    val calleeOwner: TypeNode? = null,
    val companionReceiver: TypeNode? = null,
    val declarationId: String? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newDelegate\"")
        output.append(",\"method\":")
        TypeNodeJson.writeString(method, output)
        output.append(",\"funcType\":")
        funcType.writeJson(output)
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
        output.append('}')
    }
}

data class BirNewSuspendLambda(
    val arity: Int,
    val captures: List<BirCaptureDescriptor>,
    val params: List<BirLambdaParameter>,
    val suspendRet: TypeNode,
    val typeParams: List<TypeParameterDeclaration>,
    val body: List<BirStatement>,
    val funcType: TypeNode,
    val capValues: List<BirExpression?>? = null,
    val typeArgs: List<TypeNode>? = null,
    val externalCompanionOwner: String? = null,
    val companionCaptureOwner: String? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"newSuspendLambda\"")
        output.append(",\"arity\":")
        output.append(arity)
        output.append(",\"captures\":")
        output.append('[')
        captures.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        capValues?.let { value ->
            output.append(",\"capValues\":")
            output.append('[')
            value.forEachIndexed { index0, item0 ->
                if (index0 > 0) output.append(',')
                if (item0 == null) output.append("null")
                else {
                    item0.writeJson(output)
                }
            }
            output.append(']')
        }
        output.append(",\"params\":")
        output.append('[')
        params.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"suspendRet\":")
        suspendRet.writeJson(output)
        output.append(",\"typeParams\":")
        output.append('[')
        typeParams.forEachIndexed { index0, item0 ->
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
        output.append(",\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"funcType\":")
        funcType.writeJson(output)
        externalCompanionOwner?.let { value ->
            output.append(",\"externalCompanionOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        companionCaptureOwner?.let { value ->
            output.append(",\"companionCaptureOwner\":")
            TypeNodeJson.writeString(value, output)
        }
        output.append('}')
    }
}

data class BirEventGet(
    val type: TypeNode,
    val name: String,
    val isStatic: Boolean,
    val companionCall: Boolean? = null,
    val recv: BirField<BirExpression> = BirField.Absent,
    val sty: TypeNode? = null,
    val memberVisibility: String? = null,
    val overrides: List<BirOverride>? = null,
    val companionReceiver: TypeNode? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"clrEventGet\"")
        companionCall?.let { value ->
            output.append(",\"companionCall\":")
            output.append(value)
        }
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        output.append(",\"static\":")
        output.append(isStatic)
        when (val present = recv) {
            BirField.Absent -> {}
            BirField.Null -> {
                output.append(",\"recv\":")
                output.append("null")
            }
            is BirField.Value -> {
                output.append(",\"recv\":")
                present.value.writeJson(output)
            }
        }
        sty?.let { value ->
            output.append(",\"sty\":")
            value.writeJson(output)
        }
        memberVisibility?.let { value ->
            output.append(",\"memberVisibility\":")
            TypeNodeJson.writeString(value, output)
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
        companionReceiver?.let { value ->
            output.append(",\"companionReceiver\":")
            TypeNodeJson.writeString(value.toJson(), output)
        }
        output.append('}')
    }
}

data class BirEventRaise(
    val type: TypeNode,
    val event: String,
    val isStatic: Boolean,
    val superCall: Boolean,
    val args: List<BirExpression>,
    val recv: BirExpression? = null
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"clrEventRaise\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"event\":")
        TypeNodeJson.writeString(event, output)
        output.append(",\"static\":")
        output.append(isStatic)
        output.append(",\"super\":")
        output.append(superCall)
        recv?.let { value ->
            output.append(",\"recv\":")
            value.writeJson(output)
        }
        output.append(",\"args\":")
        output.append('[')
        args.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirLambdaParameter(
    val name: String,
    val type: TypeNode
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"type\":")
        type.writeJson(output)
        output.append('}')
    }
}

data class BirInlineCapture(
    val name: String,
    val type: TypeNode,
    val outer: Boolean? = null,
    val value: BirExpression? = null,
    val sharedCellType: TypeNode? = null,
    val sharedCellTypeParams: List<TypeParameterDeclaration>? = null
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"name\":")
        TypeNodeJson.writeString(name, output)
        if (hasField) output.append(','); hasField = true; output.append("\"type\":")
        type.writeJson(output)
        outer?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"outer\":")
            output.append(value)
        }
        value?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"value\":")
            value.writeJson(output)
        }
        sharedCellType?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"sharedCellType\":")
            value.writeJson(output)
        }
        sharedCellTypeParams?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"sharedCellTypeParams\":")
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

data class BirInlineLambda(
    val params: List<BirLambdaParameter>,
    val captures: List<BirInlineCapture>,
    val body: List<BirStatement>,
    val result: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"inlineLambda\"")
        output.append(",\"params\":")
        output.append('[')
        params.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"captures\":")
        output.append('[')
        captures.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"result\":")
        result.writeJson(output)
        output.append('}')
    }
}

data class BirStackAllocation(
    val count: BirExpression,
    val elem: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"stackAlloc\"")
        output.append(",\"count\":")
        count.writeJson(output)
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append('}')
    }
}

data class BirStackGet(
    val ptr: BirExpression,
    val len: BirExpression,
    val index: BirExpression,
    val elem: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"stackGet\"")
        output.append(",\"ptr\":")
        ptr.writeJson(output)
        output.append(",\"len\":")
        len.writeJson(output)
        output.append(",\"index\":")
        index.writeJson(output)
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append('}')
    }
}

data class BirStackSet(
    val ptr: BirExpression,
    val len: BirExpression,
    val index: BirExpression,
    val elem: TypeNode,
    val value: BirExpression
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"stackSet\"")
        output.append(",\"ptr\":")
        ptr.writeJson(output)
        output.append(",\"len\":")
        len.writeJson(output)
        output.append(",\"index\":")
        index.writeJson(output)
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirStackAsSpan(
    val ptr: BirExpression,
    val len: BirExpression,
    val elem: TypeNode
) : BirExpression() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"stackAsSpan\"")
        output.append(",\"ptr\":")
        ptr.writeJson(output)
        output.append(",\"len\":")
        len.writeJson(output)
        output.append(",\"elem\":")
        elem.writeJson(output)
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
        var hasField = false
        extension?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"extension\":")
            value.writeJson(output)
        }
        dispatch?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"dispatch\":")
            value.writeJson(output)
        }
        dispatchTypeArgs?.let { value ->
            if (hasField) output.append(','); hasField = true; output.append("\"dispatchTypeArgs\":")
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
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"iface\":")
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

data class BirReturn(
    val value: BirExpression? = null
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"return\"")
        value?.let { value ->
            output.append(",\"value\":")
            value.writeJson(output)
        }
        output.append('}')
    }
}

data class BirSetLocal(
    val name: String,
    val value: BirExpression
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"setLocal\"")
        output.append(",\"name\":")
        TypeNodeJson.writeString(name, output)
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirSetField(
    val ownerType: TypeNode,
    val recv: BirExpression,
    val name: String,
    val value: BirExpression,
    val outer: Boolean? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberType: TypeNode? = null
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"setField\"")
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

data class BirBlock(
    val body: List<BirStatement>
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"block\"")
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

data class BirBranchIf(
    val id: Int,
    val on: Boolean,
    val cond: BirExpression
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"brIf\"")
        output.append(",\"id\":")
        output.append(id)
        output.append(",\"on\":")
        output.append(on)
        output.append(",\"cond\":")
        cond.writeJson(output)
        output.append('}')
    }
}

data class BirCatch(
    val excType: TypeNode,
    val variable: String,
    val body: List<BirStatement>
) : BirValue {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"excType\":")
        excType.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"var\":")
        TypeNodeJson.writeString(variable, output)
        if (hasField) output.append(','); hasField = true; output.append("\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append('}')
    }
}

data class BirTry(
    val type: TypeNode,
    val body: List<BirStatement>,
    val catches: List<BirCatch>,
    val finallyBody: List<BirStatement>? = null
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"try\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append(",\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"catches\":")
        output.append('[')
        catches.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        finallyBody?.let { value ->
            output.append(",\"finally\":")
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

data class BirForArray(
    val label: String?,
    val variable: String,
    val array: BirExpression,
    val body: List<BirStatement>
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"forArray\"")
        output.append(",\"label\":")
        if (label == null) output.append("null")
        else {
            TypeNodeJson.writeString(label, output)
        }
        output.append(",\"var\":")
        TypeNodeJson.writeString(variable, output)
        output.append(",\"array\":")
        array.writeJson(output)
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

data class BirForIn(
    val label: String?,
    val elem: TypeNode,
    val src: BirExpression,
    val srcType: TypeNode,
    val variable: String,
    val body: List<BirStatement>,
    val fallback: BirBlock
) : BirStatement() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"forIn\"")
        output.append(",\"label\":")
        if (label == null) output.append("null")
        else {
            TypeNodeJson.writeString(label, output)
        }
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append(",\"src\":")
        src.writeJson(output)
        output.append(",\"srcType\":")
        srcType.writeJson(output)
        output.append(",\"var\":")
        TypeNodeJson.writeString(variable, output)
        output.append(",\"body\":")
        output.append('[')
        body.forEachIndexed { index0, item0 ->
            if (index0 > 0) output.append(',')
            item0.writeJson(output)
        }
        output.append(']')
        output.append(",\"fallback\":")
        fallback.writeJson(output)
        output.append('}')
    }
}

data class BirConditionalBranch(
    val cond: BirExpression,
    val body: List<BirStatement>
) : BirBranch {
    override fun writeJson(output: StringBuilder) {
        output.append("{")
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"cond\":")
        cond.writeJson(output)
        if (hasField) output.append(','); hasField = true; output.append("\"body\":")
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
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"else\":")
        output.append("true")
        if (hasField) output.append(','); hasField = true; output.append("\"body\":")
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
    val joinNullBranch: Boolean? = null,
    val outer: Boolean? = null
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
        outer?.let { value ->
            output.append(",\"outer\":")
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
        var hasField = false
        if (hasField) output.append(','); hasField = true; output.append("\"f\":")
        TypeNodeJson.writeString(f, output)
        if (hasField) output.append(','); hasField = true; output.append("\"l\":")
        output.append(l)
        if (hasField) output.append(','); hasField = true; output.append("\"c\":")
        output.append(c)
        if (hasField) output.append(','); hasField = true; output.append("\"el\":")
        output.append(el)
        if (hasField) output.append(','); hasField = true; output.append("\"ec\":")
        output.append(ec)
        output.append('}')
    }
}
