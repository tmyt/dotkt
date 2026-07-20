// Migrated il batch M3 — delegated-property / lateinit-ref / reflect family. Each old case's `main` +
// stdout-golden diff becomes one @TestAttribute method whose per-value assertEquals/assertTrue/assertFalse is
// strictly stronger (typed) than the old text diff. Every value the old il_check asserted is preserved 1:1 (see
// the `// <expected>` comments). The lazy case's side-effecting `println("computing...")` (whose subject was
// SINGLE-evaluation + memoization) becomes a captured log-list counter asserted directly — the structure is unchanged.
//
// Coverage preserved (old case -> method):
//   il-lazy           -> lazyDelegate         `by lazy` member/local + isInitialized/memoization + SYNCHRONIZED/PUBLICATION/NONE/lock overloads
//   il-localdeleg     -> localDelegatedProps  IrLocalDelegatedProperty: `by lazy` local + a custom getValue/setValue delegate class
//   il-lateinitref    -> lateinitCallableRef  #66 callable ref to a PUBLIC lateinit var -> KProperty over the backing FIELD (bound + unbound)
//   il-lateinitrefpriv-> lateinitPrivateRef   #155 `this::name` over a PRIVATE lateinit -> lifted PropRef reads/writes the private field cross-class
//   il-kstar          -> kTypeProjectionStar  #82 KTypeProjection.STAR computed companion prop routes to get_STAR (star-projection toString)
//
// All top-level declarations are M3-prefixed (one project = one namespace, shared with sibling batteries + stdlib).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.reflect.KProperty
import kotlin.reflect.KTypeProjection

// ---- il-lazy : member `by lazy` (the "computing..." side effect captured into a log to prove single-eval) -------
val m3LazyLog = mutableListOf<String>()
class M3LazyConfig(val base: Int) {
    val expensive: String by lazy { m3LazyLog.add("computing..."); "VALUE" }
    val doubled: Int by lazy { base * 2 }   // captures `this` (base) -> closure
}

// ---- il-localdeleg : a duck-typed local-property delegate (getValue uppercases, setValue stores) ----------------
class M3UpperDelegate(private var v: String) {
    operator fun getValue(thisRef: Any?, property: KProperty<*>): String = v.uppercase()
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: String) { v = value }
}

// ---- il-lateinitref : callable reference to a PUBLIC lateinit var --------------------------------------------
class M3LirBox { lateinit var name: String }

// ---- il-lateinitrefpriv : #155 `this::name` over a PRIVATE lateinit ------------------------------------------
class M3LirpBox {
    private lateinit var name: String
    fun makeRef(): kotlin.reflect.KMutableProperty0<String> {
        name = "init"
        return this::name              // bound KMutableProperty0 over a PRIVATE lateinit backing field
    }
}

class MigratedM3DelegatesTests {
    @TestAttribute
    fun lazyDelegate() {
        m3LazyLog.clear()
        val c = M3LazyConfig(21)
        assertTrue(m3LazyLog.isEmpty())        // "before": the lazy initializer has NOT run at construction
        assertEquals("VALUE", c.expensive)     // VALUE (initializer runs, logs "computing...")
        assertEquals("VALUE", c.expensive)     // VALUE (memoized)
        assertEquals(1, m3LazyLog.size)        // computing... printed exactly once (single evaluation)
        assertEquals(42, c.doubled)            // 42
        assertEquals(42, c.doubled)            // 42

        var count = 0
        val lz = lazy { count++; "computed" }
        assertFalse(lz.isInitialized())        // False
        assertEquals("computed", lz.value)     // computed
        assertEquals("computed", lz.value)     // computed (memoized)
        assertTrue(lz.isInitialized())         // True
        assertEquals(1, count)                 // 1

        val local: Int by lazy { 7 * 6 }
        assertEquals(42, local)                // 42
        assertEquals(42, local)                // 42

        var s = 0
        val sync = lazy(LazyThreadSafetyMode.SYNCHRONIZED) { s++; "sync" }
        assertEquals("sync", sync.value)       // sync
        assertEquals("sync", sync.value)       // sync
        assertEquals(1, s)                     // 1

        var p = 0
        val pub = lazy(LazyThreadSafetyMode.PUBLICATION) { p++; "pub" }
        assertEquals("pub", pub.value)         // pub
        assertEquals(1, p)                     // 1

        var n = 0
        val none = lazy(LazyThreadSafetyMode.NONE) { n++; "none" }
        assertEquals("none", none.value)       // none
        assertEquals(1, n)                     // 1

        var k = 0
        val guarded = lazy(Any()) { k++; "guarded" }
        assertFalse(guarded.isInitialized())   // False
        assertEquals("guarded", guarded.value) // guarded
        assertTrue(guarded.isInitialized())    // True
        assertEquals(1, k)                     // 1
    }

    @TestAttribute
    fun localDelegatedProps() {
        val lazyVal: Int by lazy { 40 + 2 }
        assertEquals(42, lazyVal)              // 42
        assertEquals(42, lazyVal)              // 42

        var upper: String by M3UpperDelegate("hi")
        assertEquals("HI", upper)              // HI
        upper = "world"
        assertEquals("WORLD", upper)           // WORLD
    }

    @TestAttribute
    fun lateinitCallableRef() {
        val b = M3LirBox()
        b.name = "hello"
        val ref = b::name                      // bound KMutableProperty0 over a lateinit backing field
        assertEquals("hello", ref.get())       // hello
        ref.set("world")
        assertEquals("world", b.name)          // world
        assertEquals("world", ref.get())       // world

        val uref = M3LirBox::name              // unbound KMutableProperty1
        val b2 = M3LirBox()
        uref.set(b2, "unbound")
        assertEquals("unbound", uref.get(b2))  // unbound
        assertEquals("name", uref.name)        // name (KProperty.name)
    }

    @TestAttribute
    fun lateinitPrivateRef() {
        val b = M3LirpBox()
        val ref = b.makeRef()
        assertEquals("init", ref.get())        // init (lateinitGet through the lifted PropRef class)
        ref.set("changed")                     // setFieldExpr through the lifted PropRef class
        assertEquals("changed", ref.get())     // changed
        assertEquals("name", ref.name)         // name (KProperty.name)
    }

    @TestAttribute
    fun kTypeProjectionStar() {
        // variance == null -> star-projection toString
        assertEquals("*", KTypeProjection.STAR.toString())  // *
    }
}
