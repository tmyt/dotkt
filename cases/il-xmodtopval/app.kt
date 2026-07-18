// #89 (guard): a CROSS-MODULE top-level val read. `COROUTINE_SUSPENDED` (kotlin.coroutines.intrinsics,
// declared in Intrinsics.kt -> IntrinsicsKt) is a computed top-level `val` deserialized from the frontend
// metadata klib, whose parent is a package fragment (NOT an IrFile). kotc must NOT mis-attribute the read
// to the READING file's class (AppKt) — it emits owner:null (unresolved) so bir2cir binds the true
// declaring file class off the ref.dll, instead of an unresolvable `AppKt.get_COROUTINE_SUSPENDED`.
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
fun main() {
    val a: Any = COROUTINE_SUSPENDED
    val b: Any = COROUTINE_SUSPENDED
    println(a === b)                  // True  — the stable singleton box
    println(a === COROUTINE_SUSPENDED) // True
}
