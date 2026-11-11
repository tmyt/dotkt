// Generated from docs/bir-cir.schema.json by scripts/generate-ir-model.py.
// Structural model; the boundary codec is generated in TypeNodeJson.kt.
package kotc.bir

sealed class TypeNode {
    data class Fqn(val name: String, val args: List<TypeNode>? = null, val classifier: SourceClassifier? = null) : TypeNode()

    data class Tv(val scope: String, val i: Int) : TypeNode()

    data object Star : TypeNode()

    data class Projection(val variance: String, val of: TypeNode) : TypeNode()

    data class Fn(val suspend: Boolean, val ret: TypeNode, val params: List<TypeNode>, val recv: TypeNode? = null) : TypeNode()

    data class Nullable(val of: TypeNode) : TypeNode()

    data class Oblivious(val of: TypeNode) : TypeNode()

    data class Array(val elem: TypeNode) : TypeNode()

    data class ByRef(val of: TypeNode) : TypeNode()

    data class SourceClassifier(val packageName: String, val ownerPath: List<String>, val name: String)

    fun toJson(): String = TypeNodeJson.encode(this)
}
