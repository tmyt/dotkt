// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
package kotc.bir

data class Float32Literal(val bits: Int) : BirLiteral() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"encoding\":\"f32\",\"bits\":").append(bits).append('}')
    }
}

data class Float64Literal(val bits: Long) : BirLiteral() {
    override fun writeJson(output: StringBuilder) {
        output.append("{\"encoding\":\"f64\",\"bits\":").append(bits).append('}')
    }
}
