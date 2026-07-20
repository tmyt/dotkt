// Property / field-storage battery (batch M5): top-level delegated property, top-level lateinit, @Volatile fields,
// and scalar atomics. Migrates that family of cases/il-* onto the in-process NUnit suite. Each old case's `main` +
// stdout-golden diff becomes one @TestAttribute method whose per-value assert is strictly stronger (typed) than the
// old text diff; every asserted value is preserved 1:1 (see `// <expected>`). Each case owns a UNIQUE M5-prefixed
// global (delegated field / lateinit static / volatile static), so parallel discovery across DIFFERENT tests is safe.
//
// Coverage preserved (old case -> method):
//   il-topdeleg       -> topLevelDelegatedProp  #70 top-level delegated property (var by Store) -> static `x$delegate` field, null thisRef
//   il-toplateinit    -> topLevelLateinit       #104 top-level `lateinit var` (ref) static field: default-null + throw-before-init, then assign/read
//   il-volatile       -> volatileField          @Volatile -> a real CLR volatile field (value/ref instance + top-level static)
//   il-volatileatomic -> atomicVolatile         #130 scalar atomics load()/store() volatile round-trip (Int/Long/Boolean/Reference)
//
// All top-level declarations introduced here are M5-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import kotlin.reflect.KProperty
import kotlin.concurrent.Volatile
import kotlin.concurrent.atomics.AtomicInt
import kotlin.concurrent.atomics.AtomicLong
import kotlin.concurrent.atomics.AtomicBoolean
import kotlin.concurrent.atomics.AtomicReference
import kotlin.concurrent.atomics.ExperimentalAtomicApi

// ---- il-topdeleg : top-level delegated property with an arbitrary getValue/setValue provider ----------------------
class M5Store(var backing: Int) {
    operator fun getValue(thisRef: Any?, prop: KProperty<*>): Int = backing
    operator fun setValue(thisRef: Any?, prop: KProperty<*>, v: Int) { backing = v }
}
class M5ReadOnlyStore(val s: String) {
    operator fun getValue(thisRef: Any?, prop: KProperty<*>): String = s
}
var m5delegCounter by M5Store(0)
val m5delegLabel by M5ReadOnlyStore("init")

// ---- il-toplateinit : top-level `lateinit var` of a reference type ------------------------------------------------
lateinit var m5lateS: String

// ---- il-volatile : @Volatile value/reference instance fields + a top-level volatile static -----------------------
class M5Counter {
    @Volatile var value: Int = 0
    @Volatile var label: String? = null
    fun bump() { value = value + 1 }
}
@Volatile var m5globalFlag: Boolean = false

class MigratedM5PropsTests {
    @TestAttribute
    fun topLevelDelegatedProp() {
        assertEquals(0, m5delegCounter)      // 0 (getValue)
        m5delegCounter = 42
        assertEquals(42, m5delegCounter)     // 42 (setValue then getValue)
        assertEquals("init", m5delegLabel)   // init (read-only getValue)
    }

    @TestAttribute
    fun topLevelLateinit() {
        var threw = false
        try { m5lateS.length } catch (e: Exception) { threw = true }
        assertTrue(threw)                    // caught: uninitialized
        m5lateS = "hello"
        assertEquals("hello", m5lateS)       // hello
        assertEquals(5, m5lateS.length)      // 5
    }

    @TestAttribute
    fun volatileField() {
        val c = M5Counter()
        assertEquals(0, c.value)             // 0
        c.value = 41
        assertEquals(41, c.value)            // 41
        c.bump()
        assertEquals(42, c.value)            // 42
        c.label = "ready"
        assertEquals("ready", c.label)       // ready
        m5globalFlag = true
        assertTrue(m5globalFlag)             // True
    }

    @TestAttribute
    @OptIn(ExperimentalAtomicApi::class)
    fun atomicVolatile() {
        val i = AtomicInt(0)
        i.store(42)
        assertEquals(42, i.load())           // 42
        val l = AtomicLong(0L)
        l.store(9_000_000_000L)
        assertEquals(9000000000L, l.load())  // 9000000000 (> 2^32, non-tearing)
        val b = AtomicBoolean(false)
        b.store(true)
        assertTrue(b.load())                 // true
        val r = AtomicReference("a")
        r.store("b")
        assertEquals("b", r.load())          // b
    }
}
