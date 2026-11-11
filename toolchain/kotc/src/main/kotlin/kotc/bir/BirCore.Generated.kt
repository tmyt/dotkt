// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

data class BirConstant(
    val type: TypeNode,
    val value: BirLiteral
) : BirExpression {
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
) : BirExpression {
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
) : BirStatement {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"exprStmt\"")
        output.append(",\"expr\":")
        expr.writeJson(output)
        output.append('}')
    }
}

data class BirThrowExpression(
    val value: BirExpression
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"classRef\"")
        output.append(",\"type\":")
        type.writeJson(output)
        output.append('}')
    }
}

data class BirNullableNull(
    val elem: TypeNode
) : BirExpression {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"nullableNull\"")
        output.append(",\"elem\":")
        elem.writeJson(output)
        output.append('}')
    }
}

data class BirDefaultParameter(
    val idx: Int
) : BirExpression {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"defaultArgParam\"")
        output.append(",\"idx\":")
        output.append(idx)
        output.append('}')
    }
}

data class BirDefaultReceiver(
    val kind: String
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirExpression {
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
    val localCtorIndex: Int? = null,
    val memberVisibility: String? = null,
    val memberOwnerTypeParams: List<TypeParameterDeclaration>? = null,
    val memberSignature: List<TypeNode>? = null
) : BirExpression {
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
        localCtorIndex?.let { value ->
            output.append(",\"localCtorIndex\":")
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
        memberSignature?.let { value ->
            output.append(",\"memberSignature\":")
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

data class BirThrow(
    val value: BirExpression
) : BirStatement {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"throw\"")
        output.append(",\"value\":")
        value.writeJson(output)
        output.append('}')
    }
}

data class BirIf(
    val branches: List<BirBranch>
) : BirStatement {
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
) : BirStatement {
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
) : BirExpression {
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
) : BirExpression {
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
) : BirStatement {
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
) : BirStatement {
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
) : BirStatement {
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
) : BirStatement {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"k\":\"goto\"")
        output.append(",\"id\":")
        output.append(id)
        output.append('}')
    }
}

data class BirLabel(
    val id: Int
) : BirStatement {
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
