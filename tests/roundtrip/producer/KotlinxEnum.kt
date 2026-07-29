// `kotlinx.*` is an ordinary external-library namespace, not compiler- or stdlib-owned vocabulary.
package kotlinx.roundtrip.palette

// An enum with behavior exercises CLR member binding across a reference-KLIB boundary under kotlinx.*.
enum class StartMode {
    DEFAULT;

    fun marker(): Int = 42
}
