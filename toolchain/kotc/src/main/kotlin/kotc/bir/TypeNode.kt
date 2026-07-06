package kotc.bir

/**
 * The universal type representation of the BIR/CIR freeze (#37), Kotlin side.
 *
 * NORMATIVE: docs/bir-cir-spec.md §1 (the Type schema) + §4 (the shared helper API).
 * A Type is ALWAYS a JSON object with a `t` discriminator — there is NO bare-string type. Readers
 * dispatch on `t`; they NEVER split/scan a string. This sealed class is the ONE place kotc parses/
 * builds a Type, and it MUST agree byte-for-byte with the C# `DotKt.Bir.TypeNode`
 * (toolchain/bir-common/TypeNode.cs).
 *
 * ADDITIVE (phase 1b): the frozen contract in code, not yet wired to `birType()`/emit (phases 2-5).
 * JSON is hand-built compact (no spaces) with kotc's existing string-escape convention.
 */
sealed class TypeNode {
    /** `fqn`: a named type — a PURE Kotlin/CLR FQN identity; [args] = generic application. */
    data class Fqn(val name: String, val args: List<TypeNode>? = null) : TypeNode()

    /**
     * `tv`: a type variable. [scope] ∈ {"type","method"} selects the CLR generic-parameter space
     * (type → `!i` GenericTypeParameter, method → `!!i` GenericMethodParameter). [i] is owner-local:
     * for "method" the index in the method's own generic params; for "type" the FLATTENED index over
     * the enclosing-type nesting chain. The scope disambiguates the two distinct spaces.
     */
    data class Tv(val scope: String, val i: Int) : TypeNode()

    /** `fn`: a function type; [suspend] is a flag, [recv] is the extension receiver (subsumes func:/sfunc:). */
    data class Fn(
        val suspend: Boolean,
        val ret: TypeNode,
        val params: List<TypeNode>,
        val recv: TypeNode? = null,
    ) : TypeNode()

    /** `nullable`: `T?`. */
    data class Nullable(val of: TypeNode) : TypeNode()

    /** `array`: `Array<T>` (this-assembly array). */
    data class Array(val elem: TypeNode) : TypeNode()

    /** `byRef`: a CLR by-ref `ref T`. */
    data class ByRef(val of: TypeNode) : TypeNode()

    /**
     * Compact canonical JSON string of this type. Field order = required first, optional last —
     * IDENTICAL to the C# `TypeNode.ToJson` (JsonObject insertion order).
     */
    fun toJson(): String = StringBuilder().also { write(it) }.toString()

    private fun write(sb: StringBuilder) {
        when (this) {
            is Fqn -> {
                sb.append("{\"t\":\"fqn\",\"name\":").append(esc(name))
                if (args != null) {
                    sb.append(",\"args\":")
                    writeArray(sb, args)
                }
                sb.append('}')
            }
            is Tv -> sb.append("{\"t\":\"tv\",\"scope\":").append(esc(scope)).append(",\"i\":").append(i).append('}')
            is Fn -> {
                sb.append("{\"t\":\"fn\",\"suspend\":").append(if (suspend) "true" else "false")
                sb.append(",\"ret\":"); ret.write(sb)
                sb.append(",\"params\":"); writeArray(sb, params)
                if (recv != null) { sb.append(",\"recv\":"); recv.write(sb) }
                sb.append('}')
            }
            is Nullable -> { sb.append("{\"t\":\"nullable\",\"of\":"); of.write(sb); sb.append('}') }
            is Array -> { sb.append("{\"t\":\"array\",\"elem\":"); elem.write(sb); sb.append('}') }
            is ByRef -> { sb.append("{\"t\":\"byRef\",\"of\":"); of.write(sb); sb.append('}') }
        }
    }

    companion object {
        private fun writeArray(sb: StringBuilder, ts: List<TypeNode>) {
            sb.append('[')
            for ((idx, t) in ts.withIndex()) {
                if (idx > 0) sb.append(',')
                (t as TypeNode).write(sb)
            }
            sb.append(']')
        }

        /** Escape a string as a JSON string literal, matching kotc BirEmitter.str(). */
        private fun esc(s: String): String {
            val out = StringBuilder(s.length + 2)
            out.append('"')
            for (ch in s) {
                when (ch) {
                    '\\' -> out.append("\\\\")
                    '"' -> out.append("\\\"")
                    '\n' -> out.append("\\n")
                    '\r' -> out.append("\\r")
                    '\t' -> out.append("\\t")
                    '\b' -> out.append("\\b")
                    '\u000C' -> out.append("\\f")
                    else -> if (ch.code < 0x20) out.append("\\u").append(ch.code.toString(16).padStart(4, '0'))
                            else out.append(ch)
                }
            }
            out.append('"')
            return out.toString()
        }

        /** Parse a canonical type JSON string back into a [TypeNode] (real recursive-descent parse, no string-splitting). */
        fun parse(json: String): TypeNode = fromValue(JsonParser(json).parseValue())

        @Suppress("UNCHECKED_CAST")
        private fun fromValue(v: Any?): TypeNode {
            val o = v as? Map<String, Any?> ?: throw IllegalArgumentException("Type must be a JSON object, got $v")
            return when (val t = o["t"] as? String ?: throw IllegalArgumentException("Type node missing `t`")) {
                "fqn" -> Fqn(
                    o["name"] as? String ?: throw IllegalArgumentException("fqn missing name"),
                    (o["args"] as? List<Any?>)?.map { fromValue(it) },
                )
                "tv" -> Tv(
                    o["scope"] as? String ?: throw IllegalArgumentException("tv missing scope"),
                    (o["i"] as Number).toInt(),
                )
                "fn" -> Fn(
                    o["suspend"] as Boolean,
                    fromValue(o["ret"]),
                    (o["params"] as List<Any?>).map { fromValue(it) },
                    o["recv"]?.let { fromValue(it) },
                )
                "nullable" -> Nullable(fromValue(o["of"]))
                "array" -> Array(fromValue(o["elem"]))
                "byRef" -> ByRef(fromValue(o["of"]))
                else -> throw IllegalArgumentException("unknown Type discriminator `t`=\"$t\"")
            }
        }
    }
}

/** A minimal recursive-descent JSON parser (object/array/string/number/bool/null) for [TypeNode.parse]. */
private class JsonParser(private val s: String) {
    private var p = 0

    fun parseValue(): Any? {
        skipWs()
        return when (val c = peek()) {
            '{' -> parseObject()
            '[' -> parseArray()
            '"' -> parseString()
            't', 'f' -> parseBool()
            'n' -> parseNull()
            else -> if (c == '-' || c in '0'..'9') parseNumber()
                    else throw IllegalArgumentException("unexpected char '$c' at $p")
        }
    }

    private fun parseObject(): Map<String, Any?> {
        expect('{'); skipWs()
        val m = LinkedHashMap<String, Any?>()
        if (peek() == '}') { p++; return m }
        while (true) {
            skipWs()
            val key = parseString()
            skipWs(); expect(':')
            m[key] = parseValue()
            skipWs()
            when (val c = next()) {
                ',' -> continue
                '}' -> return m
                else -> throw IllegalArgumentException("expected ',' or '}' at $p, got '$c'")
            }
        }
    }

    private fun parseArray(): List<Any?> {
        expect('['); skipWs()
        val l = ArrayList<Any?>()
        if (peek() == ']') { p++; return l }
        while (true) {
            l.add(parseValue())
            skipWs()
            when (val c = next()) {
                ',' -> continue
                ']' -> return l
                else -> throw IllegalArgumentException("expected ',' or ']' at $p, got '$c'")
            }
        }
    }

    private fun parseString(): String {
        expect('"')
        val sb = StringBuilder()
        while (true) {
            when (val c = next()) {
                '"' -> return sb.toString()
                '\\' -> when (val e = next()) {
                    '"' -> sb.append('"'); '\\' -> sb.append('\\'); '/' -> sb.append('/')
                    'n' -> sb.append('\n'); 'r' -> sb.append('\r'); 't' -> sb.append('\t')
                    'b' -> sb.append('\b'); 'f' -> sb.append('\u000C')
                    'u' -> { sb.append(s.substring(p, p + 4).toInt(16).toChar()); p += 4 }
                    else -> throw IllegalArgumentException("bad escape \\$e at $p")
                }
                else -> sb.append(c)
            }
        }
    }

    private fun parseNumber(): Number {
        val start = p
        if (peek() == '-') p++
        while (p < s.length && (s[p] in '0'..'9' || s[p] == '.' || s[p] == 'e' || s[p] == 'E' || s[p] == '+' || s[p] == '-')) p++
        val tok = s.substring(start, p)
        return if (tok.any { it == '.' || it == 'e' || it == 'E' }) tok.toDouble() else tok.toLong()
    }

    private fun parseBool(): Boolean =
        if (s.startsWith("true", p)) { p += 4; true }
        else if (s.startsWith("false", p)) { p += 5; false }
        else throw IllegalArgumentException("bad literal at $p")

    private fun parseNull(): Any? {
        if (s.startsWith("null", p)) { p += 4; return null }
        throw IllegalArgumentException("bad literal at $p")
    }

    private fun peek(): Char { skipWs(); return s[p] }
    private fun next(): Char = s[p++]
    private fun expect(c: Char) { if (s[p] != c) throw IllegalArgumentException("expected '$c' at $p, got '${s[p]}'"); p++ }
    private fun skipWs() { while (p < s.length && s[p].isWhitespace()) p++ }
}
