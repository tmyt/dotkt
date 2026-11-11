// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

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
            TypeNodeJson.write(value, output)
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
            TypeNodeJson.write(value, output)
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
        else TypeNodeJson.writeString(label, output)
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
        else TypeNodeJson.writeString(label, output)
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

data class BirSourceSpan(
    val f: String,
    val l: Int,
    val c: Int,
    val el: Int,
    val ec: Int
) {
    fun writeJson(output: StringBuilder) {
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
