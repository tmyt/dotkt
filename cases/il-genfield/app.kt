// R4 (#91): generic FIELD token anchoring. A raw `@ClrField` access whose owner is a generic type must reference
// the field on a CONSTRUCTED instantiation, never the open def — a bare `C`1::f` operand is "not fully
// instantiated" (ilemit: `field must be declared on a generic type definition`, and ilverify crashes in
// get_GenericParameters, IndexOutOfRange). Mirror of the #84-I METHOD-side generic-base anchoring, FIELD side.
// SUSPEND-FREE by design (the fault is pure Reflection.Emit token mechanics; the kotlinx port hit it via
// `JobSupport.kt ResumeAwaitOnCompletion`1.invoke [this]`). Covers all three anchoring axes:
//   (a) self-instantiation, OWN field via `this` inside a generic method                (Cell.read/replace)
//   (b) self-instantiation, INHERITED generic-base field via `this` (the #91 core)      (Wrap.peek/put)
//   (c) inherited generic-base field via a NON-generic subclass, constructed receiver   (IntBox.slot)
//   (d) inherited generic-base field via a GENERIC subclass, constructed receiver       (Sub<String>.slot)
annotation class ClrField   // recognized by short name -> @ClrField => a raw `field` node (no property getter)

open class Base<T>(v: T) {
    @ClrField var slot: T = v            // plain CLR field on a GENERIC base
}

class Cell<T>(v: T) {
    @ClrField var item: T = v            // plain CLR field on a GENERIC type
    fun read(): T = item                 // (a) self-instantiation own field via `this`
    fun replace(x: T): T { val old = item; item = x; return old }
}

class Wrap<T>(v: T) : Base<T>(v) {
    fun peek(): T = slot                 // (b) self-instantiation INHERITED generic-base field via `this` (#91 core)
    fun put(x: T) { slot = x }
}

class IntBox(v: Int) : Base<Int>(v)      // NON-generic subclass of a generic base
class Sub<T>(v: T) : Base<T>(v)          // GENERIC subclass of a generic base

fun main() {
    val c = Cell(41)
    println(c.read())                    // 41
    println(c.replace(42))               // 41  (old)
    println(c.read())                    // 42

    val w = Wrap(100)
    println(w.peek())                    // 100
    w.put(101)
    println(w.peek())                    // 101

    val ib = IntBox(7)                   // (c) constructed receiver, non-generic subclass
    println(ib.slot)                     // 7
    ib.slot = 8
    println(ib.slot)                     // 8

    val s = Sub("hi")                    // (d) constructed receiver, generic subclass
    println(s.slot)                      // hi
    s.slot = "bye"
    println(s.slot)                      // bye
}
