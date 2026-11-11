package kotc.bir

// A String is a sequence of UTF-16 code units, independently of its runtime ABI.
internal object LiteralText {
    fun requiresCodeUnits(value: String): Boolean {
        var i = 0
        while (i < value.length) {
            if (value[i].isSurrogate()) {
                if (value[i].isHighSurrogate() && i + 1 < value.length && value[i + 1].isLowSurrogate()) i++
                else return true
            }
            i++
        }
        return false
    }

    fun writeJson(value: String, output: StringBuilder) {
        if (!requiresCodeUnits(value)) { TypeNodeJson.writeString(value, output); return }
        output.append("{\"encoding\":\"utf16\",\"units\":[")
        value.forEachIndexed { index, unit ->
            if (index > 0) output.append(',')
            output.append(unit.code)
        }
        output.append("]}")
    }
}
