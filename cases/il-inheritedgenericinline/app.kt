// #88 — splicing an INHERITED member `inline fun` whose OWNER class is GENERIC. `Container<E>.transform` is a member
// inline fn on a generic owner; `IntBox : Container<Int>` inherits it. Calling it on an IntBox receiver takes the
// same-module member-inline splice path (a lambda arg -> callNeedsSplice). The callee's OWNER is Container (not the
// receiver's static class IntBox), so the pre-#88 F2A guard OMITTED Container's type args -> the spliced body's
// `tv{scope:type,0}` (E) stayed OPEN -> ilemit typed the dispatch temp as the open generic -> BadImageFormatException.
// kotc's F2A now carries the owner's args via the corresponding-supertype instantiation Container<Int>, so
// tv{scope:type,0} concretizes to Int32.
abstract class Container<E>(val value: E) {
    inline fun transform(block: (E) -> E): E = block(value)
}

class IntBox(v: Int) : Container<Int>(v)

// A second inherited-inline site whose owner arg is a REFERENCE type (String), so the fix is exercised for both a
// value-type and a reference-type owner instantiation (the value-type path is the one that BadImageFormats).
class StrBox(v: String) : Container<String>(v)

// The dispatch receiver is a TYPE PARAMETER whose bound fixes the owner instantiation (`T : Container<Int>`). F2A's
// corresponding-supertype resolution reads the bound (not just a class-subtype receiver), so tv{scope:type,0} still
// concretizes to Int32 — the same fix one axis over (findCorrespondingSupertypes handles a type-parameter subtype).
fun <T : Container<Int>> viaBound(t: T): Int = t.transform { it + 12 }

fun main() {
    val ib = IntBox(20)
    println(ib.transform { it + 22 })        // 42

    val sb = StrBox("ab")
    println(sb.transform { it + "cd" })       // abcd

    println(viaBound(IntBox(30)))            // 42
}
