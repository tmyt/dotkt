// Enum battery — migrates the Kotlin `enum class` family of cases/il-* onto the in-process NUnit suite.
// Related old cases are consolidated by enum compiler shape into typed assertEquals/assertTrue/assertFalse
// methods. Ordered side-effecting `println`s (values()/enumValues loops) are captured into a log list and
// asserted in order.
//
// EXCLUDED from this family (matched the enum grep but the real subject is .NET interop, not Kotlin
// `enum class` behavior — kept in the bash lane):
//   il-netenum       -> for-loop over a raw .NET IEnumerable<T> (imports Kfc.*, ships runtime.cs;
//                       CLR-interop lane, not an enum-class subject)
//   il-netenumbound  -> a reference-KLIB-projected .NET enum (System.DayOfWeek) satisfies `T : Enum<T>`
//                       (il_check_imports .NET-interop lane; subject is .NET-enum binding, not Kotlin enums)
//
// Coverage preserved (old case -> method):
//   il-enum       -> enum_whenOverEnum          basic enum + `when` over enum -> String
//   il-enumbody   -> enumbody_perEntryBody       per-entry bodies overriding an abstract member (values/valueOf/name)
//   il-enumintr   -> enumintr_enumValuesValueOf  reified enumValues<T>/enumValueOf<T> (index/.size/ordinal/loop) + reified-inline callee
//   m-a8          -> enumValuesValueOf            enum entries collection size (other enum API assertions are covered here/ctorAndMethod)
//   il-enumrich   -> enumrich_ctorAndMethod      rich enum (ctor param + instance method) singleton lowering (mass/heavy/name/ordinal/valueOf/values/==)
//   il-enumtostr  -> enumtostr_inheritedMembers  basic enum inherits ToString/Equals/GetHashCode from System.Enum (toString/println/concat/==/equals/compareTo); decl in sibling EnumCrossFileSupport.kt
//   #279          -> mixedEntryBodies             rich enum mixing an entry subclass with a direct base instance
//   #478          -> entryOwnedStateAndInitializers  entry-body fields and initializer blocks in declaration order
//   #482          -> entryOwnedStateAndInitializers  direct subscribe/close on locally synthesized CLR events
//   #480          -> baseStateAndInitializers      rich-enum base fields and init blocks run before entry-body initialization
//   #479          -> implementedInterfaces         rich enum preserves constructed interface slots and defaults
//   #490          -> emptyRichEnum                  zero-entry rich enum emits valid values/valueOf bodies
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and
// `Enum`-prefixed so the two `enum class Color { RED, GREEN, BLUE }` (il-enum vs il-enumintr) don't clash.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

// ---- il-enum : basic enum + `when` over enum -----------------------------------------------------------------
enum class EnumWhenColor { RED, GREEN, BLUE }
fun enumColorName(c: EnumWhenColor): String = when (c) {
    EnumWhenColor.RED -> "red"
    EnumWhenColor.GREEN -> "green"
    else -> "blue"
}

// ---- il-enumbody : per-entry bodies overriding an abstract member --------------------------------------------
enum class EnumOp(val sym: String) {
    PLUS("+")  { override fun apply(a: Int, b: Int) = a + b },
    MINUS("-") { override fun apply(a: Int, b: Int) = a - b },
    TIMES("*") { override fun apply(a: Int, b: Int) = a * b };
    abstract fun apply(a: Int, b: Int): Int
}

enum class EnumMixedEntryBody {
    SPECIAL { override fun label() = "special" },
    PLAIN;

    open fun label(): String = "plain"
}

enum class EnumMixedEntryProperty {
    SPECIAL { override val label: String get() = "special" },
    PLAIN;

    open val label: String get() = "plain"
}

enum class EnumAbstractEntryProperty {
    A { override val label: String get() = "a" },
    B { override val label: String get() = "b" };

    abstract val label: String
}

enum class EnumAbstractEntryVar {
    A {
        override var label: String
            get() = "a"
            set(value) { value.length }
    };

    abstract var label: String
}

object EnumEntryStateLog {
    var text = ""
    fun next(mark: String): Int { text += mark; return text.length }
    fun add(mark: String) { text += mark }
}

enum class EnumEntryOwnedState(val base: Int) {
    A(EnumEntryStateLog.next("b")) {
        val first = EnumEntryStateLog.next("p")
        init { EnumEntryStateLog.add("i$first") }
        var second = EnumEntryStateLog.next("q")
        init {
            second += first
            EnumEntryStateLog.add("j$second")
        }

        override fun snapshot(): String = "$first:$second"
    };

    abstract fun snapshot(): String
}

enum class EnumEntryOverrideState {
    A { override val value = 11 },
    B { override val value = 22 };

    abstract val value: Int
}

enum class EnumEntryOwnedEvent {
    A {
        val pulse: ClrEvent<(Int) -> Unit> by clrEvent()

        override fun exercise(): Int {
            var seen = 0
            val subscription = pulse.subscribe { seen += it }
            pulse.invoke(5)
            subscription.close()
            pulse.invoke(7)
            return seen
        }
    };

    abstract fun exercise(): Int
}

object EnumBaseStateLog {
    var text = ""
    fun next(mark: String): Int { text += mark; return text.length }
    fun add(mark: String) { text += mark }
}

enum class EnumBaseOwnedState(val arg: Int) {
    A(EnumBaseStateLog.next("a")),
    B(EnumBaseStateLog.next("b")) {
        val entry = EnumBaseStateLog.next("e")
        init { EnumBaseStateLog.add("f$entry") }

        override fun entryState(): Int = entry
    };

    val first = EnumBaseStateLog.next("p")
    init { EnumBaseStateLog.add("i$first") }
    val second = EnumBaseStateLog.next("q")
    init { EnumBaseStateLog.add("j$second") }

    open fun entryState(): Int = 0
}

enum class EnumNonPropertyParam(arg: Int) {
    A(3);

    val doubled = arg * 2
}

object EnumBodyOnlyLog {
    var text = ""
    fun next(mark: String): Int { text += mark; return text.length }
    fun add(mark: String) { text += mark }
}

enum class EnumBodyOnlyState {
    A, B;

    val initialized = EnumBodyOnlyLog.next(name)
    init { EnumBodyOnlyLog.add("$ordinal:$initialized") }
    val computed: Int get() = ordinal + 10
}

enum class EnumComputedOnlyState {
    A;

    val computed: Int get() = ordinal + 20
}

object EnumInitOnlyLog {
    var total = 0
}

enum class EnumInitOnlyState {
    A, B;

    init { EnumInitOnlyLog.total += ordinal + 1 }
}

interface EnumRichContract<T> {
    fun value(): T
    val item: T
    fun defaultValue(): T = item
    val defaultItem: T get() = item
}

enum class EnumInterfaceState(private val n: Int) : EnumRichContract<Int> {
    A(7), B(11);

    override fun value(): Int = n
    override val item: Int get() = n + 1
}

fun enumInterfaceSnapshot(value: EnumRichContract<Int>): String =
    "${value.value()}:${value.item}:${value.defaultValue()}:${value.defaultItem}"

interface EnumDefaultOnlyContract {
    fun defaultValue(): Int = 13
    val defaultItem: Int get() = 14
}

enum class EnumDefaultOnlyState : EnumDefaultOnlyContract { A }

interface EnumEntryPropertyContract {
    val item: Int
}

enum class EnumEntryPropertyState : EnumEntryPropertyContract {
    A { override val item: Int get() = 21 },
    B { override val item: Int get() = 22 }
}

interface EnumDelegatedContract {
    fun value(): Int
    val item: Int
}

class EnumDelegatedContractImpl(private val n: Int) : EnumDelegatedContract {
    override fun value(): Int = n
    override val item: Int get() = n + 1
}

enum class EnumDelegatingState(delegate: EnumDelegatedContract) : EnumDelegatedContract by delegate {
    A(EnumDelegatedContractImpl(31)), B(EnumDelegatedContractImpl(41))
}

// ---- il-enumintr : basic enum + reified enumValues/enumValueOf intrinsics -------------------------------------
enum class EnumIntrColor { RED, GREEN, BLUE }
inline fun <reified T : Enum<T>> enumPick(i: Int): T = enumValues<T>()[i]

// ---- il-enumrich : rich enum (ctor param + instance method) --------------------------------------------------
enum class EnumPlanet(val mass: Int) {
    EARTH(5), MARS(1), JUPITER(9);
    fun heavy(): Boolean = mass > 3
}

enum class EnumEmptyRich {
    ;
    fun marker(): Int = 1
}

enum class EnumSecondaryState(val value: Int, val label: String = "number:$value") {
    PRIMARY(1, "primary"),
    NUMBER("xx", true),
    TEXT("word"),
    CHAINED(true),
    DEFAULTED(value = 5L),
    BODY("1234567", true) { override fun marker(): String = "body:$label" };

    var path = "initialized"

    constructor(input: String, secondary: Boolean) : this(input.length) { path += if (secondary) ":int" else ":unused" }
    constructor(__name: String) : this(__name.length, __name) { path += ":string" }
    constructor(__ordinal: Boolean) : this(if (__ordinal) "yes" else "no") { path += ":bool" }
    constructor(label: String = "long", value: Long) : this(value.toInt(), label) { path += ":long" }

    open fun marker(): String = "$value:$label"
}

class EnumTests {
    @TestAttribute
    fun whenOverEnum() {
        assertEquals("red", enumColorName(EnumWhenColor.RED))     // red
        assertEquals("green", enumColorName(EnumWhenColor.GREEN)) // green
        assertEquals("blue", enumColorName(EnumWhenColor.BLUE))   // blue
    }

    @TestAttribute
    fun perEntryBody() {
        val log = mutableListOf<String>()
        for (op in EnumOp.values()) log.add(op.sym + ": " + op.apply(6, 2))
        assertEquals("+: 8|-: 4|*: 12", log.joinToString("|"))    // +: 8 / -: 4 / *: 12
        assertEquals("PLUS", EnumOp.PLUS.name)                    // PLUS
        assertEquals(9, EnumOp.valueOf("TIMES").apply(3, 3))      // 9
    }

    @TestAttribute
    fun mixedEntryBodies() {
        assertEquals("special", EnumMixedEntryBody.SPECIAL.label())
        assertEquals("plain", EnumMixedEntryBody.PLAIN.label())
        assertEquals("plain", EnumMixedEntryBody.valueOf("PLAIN").label())
        assertEquals("SPECIAL|PLAIN", EnumMixedEntryBody.values().joinToString("|"))

        assertEquals("special", EnumMixedEntryProperty.SPECIAL.label)
        assertEquals("plain", EnumMixedEntryProperty.PLAIN.label)

        assertEquals("a", EnumAbstractEntryProperty.A.label)
        assertEquals("b", EnumAbstractEntryProperty.B.label)

        EnumAbstractEntryVar.A.label = "ignored"
        assertEquals("a", EnumAbstractEntryVar.A.label)
    }

    @TestAttribute
    fun entryOwnedStateAndInitializers() {
        assertEquals(1, EnumEntryOwnedState.A.base)
        assertEquals("2:7", EnumEntryOwnedState.A.snapshot())
        assertEquals("bpi2qj7", EnumEntryStateLog.text)
        assertEquals(11, EnumEntryOverrideState.A.value)
        assertEquals(22, EnumEntryOverrideState.B.value)
        assertEquals(5, EnumEntryOwnedEvent.A.exercise())

        val named = EnumNamedOwnedEvent()
        var seen = 0
        val subscription = named.pulse.subscribe { seen += it }
        named.raise(3)
        subscription.close()
        named.raise(7)
        assertEquals(3, seen)

        val derived = EnumDerivedOwnedEvent()
        var inheritedSeen = 0
        val inheritedSubscription = derived.pulse.subscribe { inheritedSeen += it }
        derived.raise(4)
        inheritedSubscription.close()
        derived.raise(6)
        assertEquals(4, inheritedSeen)

        assertEquals(8, exerciseGenericLocalEvent(EnumDerivedOwnedEvent()))

        val generic = EnumGenericOwnedEvent<String>()
        var genericSeen = ""
        val genericSubscription = generic.pulse.subscribe { genericSeen += it }
        generic.raise("a")
        genericSubscription.close()
        generic.raise("b")
        assertEquals("a", genericSeen)
        assertEquals(1, exerciseGenericOwnerConstraint(generic, "c"))
        assertEquals(5, exerciseConstraintOnlyLocalEvent(EnumConstraintOnlyOwnedEvent<String>(), "marker"))
    }

    @TestAttribute
    fun baseStateAndInitializers() {
        assertEquals(1, EnumBaseOwnedState.A.arg)
        assertEquals(2, EnumBaseOwnedState.A.first)
        assertEquals(5, EnumBaseOwnedState.A.second)
        assertEquals(0, EnumBaseOwnedState.A.entryState())
        assertEquals(8, EnumBaseOwnedState.B.arg)
        assertEquals(9, EnumBaseOwnedState.B.first)
        assertEquals(12, EnumBaseOwnedState.B.second)
        assertEquals(16, EnumBaseOwnedState.B.entryState())
        assertEquals("api2qj5bpi9qj12ef16", EnumBaseStateLog.text)
        assertEquals(6, EnumNonPropertyParam.A.doubled)
        assertEquals(1, EnumBodyOnlyState.A.initialized)
        assertEquals(5, EnumBodyOnlyState.B.initialized)
        assertEquals(10, EnumBodyOnlyState.A.computed)
        assertEquals(11, EnumBodyOnlyState.B.computed)
        assertEquals("A0:1B1:5", EnumBodyOnlyLog.text)
        assertEquals(20, EnumComputedOnlyState.A.computed)
        assertEquals(1, EnumInitOnlyState.B.ordinal)
        assertEquals(3, EnumInitOnlyLog.total)
    }

    @TestAttribute
    fun implementedInterfaces() {
        val erased: Any = EnumInterfaceState.A
        assertTrue(erased is EnumRichContract<*>)
        assertEquals("7:8:8:8", enumInterfaceSnapshot(EnumInterfaceState.A))
        assertEquals("11:12:12:12", enumInterfaceSnapshot(EnumInterfaceState.B))

        val defaultOnly: Any = EnumDefaultOnlyState.A
        assertTrue(defaultOnly is EnumDefaultOnlyContract)
        assertEquals(13, (defaultOnly as EnumDefaultOnlyContract).defaultValue())
        assertEquals(14, defaultOnly.defaultItem)

        val entryPropertyA: EnumEntryPropertyContract = EnumEntryPropertyState.A
        val entryPropertyB: EnumEntryPropertyContract = EnumEntryPropertyState.B
        assertEquals(21, entryPropertyA.item)
        assertEquals(22, entryPropertyB.item)

        val delegatedA: EnumDelegatedContract = EnumDelegatingState.A
        val delegatedB: EnumDelegatedContract = EnumDelegatingState.B
        assertEquals(31, delegatedA.value())
        assertEquals(32, delegatedA.item)
        assertEquals(41, delegatedB.value())
        assertEquals(42, delegatedB.item)
    }

    @TestAttribute
    fun enumValuesValueOf() {
        assertEquals(EnumIntrColor.GREEN, enumValues<EnumIntrColor>()[1])       // GREEN
        assertEquals(3, enumValues<EnumIntrColor>().size)                       // 3
        assertEquals(2, enumValueOf<EnumIntrColor>("BLUE").ordinal)             // 2
        val log = mutableListOf<String>()
        for (c in enumValues<EnumIntrColor>()) log.add(c.toString())           // RED / GREEN / BLUE
        assertEquals("RED|GREEN|BLUE", log.joinToString("|"))
        assertEquals(EnumIntrColor.BLUE, enumPick<EnumIntrColor>(2))            // BLUE (reified-inline callee)
        assertEquals(3, EnumIntrColor.entries.size)                              // 3 (entries collection)
    }

    @TestAttribute
    fun ctorAndMethod() {
        assertEquals(5, EnumPlanet.EARTH.mass)                    // 5
        assertTrue(EnumPlanet.EARTH.heavy())                     // True
        assertFalse(EnumPlanet.MARS.heavy())                    // False
        assertEquals("JUPITER", EnumPlanet.JUPITER.name)         // JUPITER
        assertEquals(1, EnumPlanet.MARS.ordinal)                 // 1
        assertEquals(9, EnumPlanet.valueOf("JUPITER").mass)      // 9
        val log = mutableListOf<String>()
        for (p in EnumPlanet.values()) log.add(p.name)           // EARTH / MARS / JUPITER
        assertEquals("EARTH|MARS|JUPITER", log.joinToString("|"))
        assertTrue(EnumPlanet.EARTH == EnumPlanet.EARTH)        // True
        assertFalse(EnumPlanet.EARTH == EnumPlanet.MARS)       // False

        assertEquals("1:primary", EnumSecondaryState.PRIMARY.marker())
        assertEquals("2:number:2", EnumSecondaryState.NUMBER.marker())
        assertEquals("4:word", EnumSecondaryState.TEXT.marker())
        assertEquals("3:yes", EnumSecondaryState.CHAINED.marker())
        assertEquals("5:long", EnumSecondaryState.DEFAULTED.marker())
        assertEquals("body:number:7", EnumSecondaryState.BODY.marker())
        assertEquals("initialized", EnumSecondaryState.PRIMARY.path)
        assertEquals("initialized:int", EnumSecondaryState.NUMBER.path)
        assertEquals("initialized:string", EnumSecondaryState.TEXT.path)
        assertEquals("initialized:string:bool", EnumSecondaryState.CHAINED.path)
        assertEquals("initialized:long", EnumSecondaryState.DEFAULTED.path)
        assertEquals("initialized:int", EnumSecondaryState.BODY.path)
        assertEquals("TEXT", EnumSecondaryState.TEXT.name)
        assertEquals(2, EnumSecondaryState.TEXT.ordinal)
        assertEquals("CHAINED", EnumSecondaryState.CHAINED.toString())
        assertEquals(3, EnumSecondaryState.CHAINED.ordinal)
        assertEquals(6, EnumSecondaryState.values().size)
        assertEquals(EnumSecondaryState.BODY, EnumSecondaryState.valueOf("BODY"))
    }

    @TestAttribute
    fun emptyRichEnum() {
        assertEquals(0, EnumEmptyRich.values().size)
        assertEquals(0, enumValues<EnumEmptyRich>().size)
        val missing = try {
            EnumEmptyRich.valueOf("MISSING")
            "no-throw"
        } catch (e: IllegalArgumentException) {
            "iae"
        }
        assertEquals("iae", missing)
    }

    @TestAttribute
    fun inheritedMembers() {
        // EnumBasic is declared in the SIBLING file EnumCrossFileSupport.kt (same assembly) — the #90 cross-file
        // module-wide basic-enum collection. All members are INHERITED from System.Enum.
        assertEquals("A", EnumBasic.A.toString())                // A  (explicit .toString())
        assertEquals("B", EnumBasic.B.toString())                // B  (println(Any?) -> toString)
        assertEquals("C", "" + EnumBasic.C)                      // C  (string concat)
        assertFalse(EnumBasic.A == EnumBasic.B)                 // False
        assertTrue(EnumBasic.A.equals(EnumBasic.A))            // True
        assertEquals(-2, EnumBasic.A.compareTo(EnumBasic.C))    // -2
    }
}
