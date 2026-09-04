package kotc.bir

/**
 * The universal type representation of the BIR/CIR freeze (#37), Kotlin side.
 *
 * NORMATIVE: docs/bir-cir-spec.md §1 (the Type schema) + §4 (the shared helper API).
 * A Type is ALWAYS a JSON object with a `t` discriminator — there is NO bare-string type. This
 * sealed class is the ONE place kotc builds and serializes a Type, and it MUST agree byte-for-byte
 * with the C# `DotKt.Bir.TypeNode`
 * (toolchain/bir-common/TypeNode.cs).
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

    /** `star`: a Kotlin `*` projection. BIR preserves it; bir2cir chooses an existential CLR representation. */
    data object Star : TypeNode()

    /** `projection`: a Kotlin use-site `in T` / `out T` projection. bir2cir chooses its CLR representation. */
    data class Projection(val variance: String, val of: TypeNode) : TypeNode() {
        init { require(variance == "in" || variance == "out") }
    }

    /** `fn`: a function type; [suspend] is a flag, [recv] is the extension receiver (subsumes func:/sfunc:). */
    /**
     * `fn`: a Kotlin function type. `recv` + `params` is the physical `FunctionN` argument order.
     *
     * `ctx` = the Kotlin CONTEXT parameters of a context function type (`context(A) B.(D) -> E` reads back as
     * `ctx=[A]`, `recv=B`, `params=[D]`). dll2klib consumes the equivalent assembly metadata when producing
     * KLIB declarations; kotc's own BIR carries the fact in the declaration-slot key `ctxFnType`, because a
     * type node is rebuilt by many bir2cir passes and would lose it.
     */
    data class Fn(
        val suspend: Boolean,
        val ret: TypeNode,
        val params: List<TypeNode>,
        val recv: TypeNode? = null,
        val ctx: List<TypeNode> = emptyList(),
    ) : TypeNode()

    /** `nullable`: `T?` (NullableAttribute=2). */
    data class Nullable(val of: TypeNode) : TypeNode()

    /**
     * `oblivious`: `T!` — an NRT-oblivious reference type (NullableAttribute=0), the flexible/platform
     * `(T..T?)` (spec §1 tri-state nullability). A sibling of [Nullable] with the same `{of:T}` shape.
     * Reference KLIB metadata maps it to a frontend flexible type. Frontend-only — kotc BIR normally
     * resolves it to not-null/nullable before the backend.
     */
    data class Oblivious(val of: TypeNode) : TypeNode()

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
            Star -> sb.append("{\"t\":\"star\"}")
            is Projection -> {
                sb.append("{\"t\":\"projection\",\"variance\":").append(esc(variance)).append(",\"of\":")
                of.write(sb)
                sb.append('}')
            }
            is Fn -> {
                sb.append("{\"t\":\"fn\",\"suspend\":").append(if (suspend) "true" else "false")
                sb.append(",\"ret\":"); ret.write(sb)
                sb.append(",\"params\":"); writeArray(sb, params)
                if (recv != null) { sb.append(",\"recv\":"); recv.write(sb) }
                sb.append('}')
            }
            is Nullable -> { sb.append("{\"t\":\"nullable\",\"of\":"); of.write(sb); sb.append('}') }
            is Oblivious -> { sb.append("{\"t\":\"oblivious\",\"of\":"); of.write(sb); sb.append('}') }
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

    }
}
