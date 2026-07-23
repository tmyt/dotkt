// `kotlinx.*` is an ordinary external-library namespace, not compiler- or stdlib-owned vocabulary.
package kotlinx.roundtrip.palette

// An enum with behavior is emitted as a class-like Kotlin enum. Its entry is re-imported as an injected static
// property/field access, exercising CLR member binding across the DLL boundary under the kotlinx namespace.
enum class StartMode {
    DEFAULT;

    fun marker(): Int = 42
}
