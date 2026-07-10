// #82: KTypeProjection.Companion.STAR is a COMPUTED companion property (`val STAR: KTypeProjection get() =
// star`). A cross-module deserialized IR stub spuriously reports STAR.backingField != null, which used to
// route the read through the `staticField STAR` else-branch -> ilemit "static field STAR not found". The
// getter-kind gate (the deserialized FirDefaultPropertyGetter discriminator) keeps the computed property on
// the get_STAR accessor path, while the sibling `star` (a real backing-field val) still reads as a field.
import kotlin.reflect.KTypeProjection

fun main() {
    println(KTypeProjection.STAR)   // "*" (variance == null -> star-projection toString)
}
