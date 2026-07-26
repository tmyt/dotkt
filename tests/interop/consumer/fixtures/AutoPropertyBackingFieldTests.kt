// #228 — AUTO-PROPERTY BACKING-FIELD SHAPE. A Kotlin property with default accessors becomes a real CLR property; its
// storage must carry a compiler-generated, unspeakable metadata name (`<Value>k__BackingField`, the C# convention), not
// the property's own name. Emitting both under one name made the type unusable through reflection-driven .NET
// libraries: Newtonsoft groups candidate members by name, so `SerializeObject` on such a type silently produced `{}`
// and the round-trip back threw on the null constructor argument.
//
// A property whose storage IS the user-visible member (`lateinit var`, `const`, a delegated `p$delegate`, a
// companion/top-level static, the `@ClrField` opt-out) emits no CLR property and therefore keeps its plain name — the
// last test pins that boundary. A CUSTOM accessor over a backing field is still accessor-routed, so it IS renamed.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNotNull as assertNotNull
import System.Type
import System.Reflection.FieldInfo
import System.Reflection.RuntimeReflectionExtensions.GetRuntimeFields
import System.Reflection.RuntimeReflectionExtensions.GetRuntimeProperties
import Newtonsoft.Json.JsonConvert

class BfCarrier(var Name: String, var Age: Int) {
    var Value: Int = 0
}

class BfLateinitHolder {
    lateinit var Late: String
}

// An `override var` declares its OWN storage, so both classes carry a distinctly-named backing field and the derived
// accessor must not reach the base's. `Own` is declared only on the base and read through a derived receiver.
open class BfBase(open var Shared: Int) {
    var Own: String = "own"
}

class BfDerived(seed: Int) : BfBase(seed) {
    override var Shared: Int
        get() = super.Shared * 2
        set(v) { super.Shared = v }
}

// A CUSTOM accessor over a backing field: still a CLR property, so still renamed.
class BfCustomAccessor {
    var Scaled: Int = 0
        get() = field + 100
        set(v) { field = v * 2 }
}

object BfSingleton {
    var Slot: Int = 1
}

enum class BfRich(val Weight: Int) { A(1), B(2) }

// `GetRuntimeFields`/`GetRuntimeProperties` (unlike the flag-less `GetFields`/`GetMember` overloads) include the
// non-public instance members, which is where an accessor-routed backing field lives.
private fun fieldNames(t: Type): List<String> {
    val names = mutableListOf<String>()
    for (f in t.GetRuntimeFields()) names.add(f.Name)
    return names
}

private fun propertyNames(t: Type): List<String> {
    val names = mutableListOf<String>()
    for (p in t.GetRuntimeProperties()) names.add(p.Name)
    return names
}

private fun backingFieldOf(t: Type, name: String): FieldInfo? {
    for (f in t.GetRuntimeFields()) if (f.Name == name) return f
    return null
}

class AutoPropertyBackingFieldTests {
    // (a) No CLR member name is carried by BOTH a field and a property, and each backing field uses the unspeakable
    // `<Prop>k__BackingField` spelling — `<`/`>` cannot appear in a Kotlin identifier (not even backtick-quoted), so no
    // user declaration can ever collide with one.
    @TestAttribute
    fun backingFieldsAreUnspeakableAndDistinctFromTheProperty() {
        val t = Type.GetType("BfCarrier")!!
        val fields = fieldNames(t)
        val props = propertyNames(t)
        assertEquals(3, fields.size)
        assertEquals(3, props.size)
        for (p in props) assertFalse(fields.contains(p))
        assertTrue(props.contains("Value"))
        assertTrue(fields.contains("<Value>k__BackingField"))
        assertTrue(fields.contains("<Name>k__BackingField"))
        assertTrue(fields.contains("<Age>k__BackingField"))
    }

    // (b) The accessors really read/write THAT field: a reflection write to the synthesized field is observed through
    // the getter, and a property write is observed in the field.
    @TestAttribute
    fun accessorsReadAndWriteTheSynthesizedField() {
        val t = Type.GetType("BfCarrier")!!
        val backing = backingFieldOf(t, "<Value>k__BackingField")
        assertNotNull(backing)
        val carrier = BfCarrier("n", 1)
        carrier.Value = 7
        assertEquals(7, carrier.Value)
        backing!!.SetValue(carrier, 99)
        assertEquals(99, carrier.Value)
        carrier.Value = 5
        assertEquals(5, backing.GetValue(carrier) as Int)
    }

    // (c) The reported symptom: JSON.NET must serialize the properties and deserialize them back. Asserted
    // member-by-member (not as one exact string) so the test does not pin Newtonsoft's member ORDER, which nothing in
    // the contract fixes; the `{}` regression is caught by every one of the three fragments going missing.
    @TestAttribute
    fun jsonNetSerializesAndDeserializesAnAutoPropertyType() {
        val original = BfCarrier("ada", 36)
        original.Value = 7
        val json = JsonConvert.SerializeObject(original)
        assertTrue(json.contains("\"Name\":\"ada\""))
        assertTrue(json.contains("\"Age\":36"))
        assertTrue(json.contains("\"Value\":7"))
        val back = JsonConvert.DeserializeObject(json, Type.GetType("BfCarrier")!!) as BfCarrier
        assertEquals("ada", back.Name)
        assertEquals(36, back.Age)
        assertEquals(7, back.Value)
    }

    // An `override var` re-declares its OWN storage: base and derived each carry their own renamed field, the derived
    // accessor reaches the base's only through `super`, and a base-declared property read through a DERIVED receiver
    // still resolves. This is what the pass's owner->base chain walk exists for.
    @TestAttribute
    fun overriddenAndInheritedPropertiesKeepDistinctStorage() {
        val baseFields = fieldNames(Type.GetType("BfBase")!!)
        assertTrue(baseFields.contains("<Shared>k__BackingField"))
        assertTrue(baseFields.contains("<Own>k__BackingField"))
        assertFalse(baseFields.contains("Shared"))
        assertFalse(baseFields.contains("Own"))

        val d = BfDerived(5)
        assertEquals(10, d.Shared)      // derived getter: base storage * 2
        d.Shared = 10
        assertEquals(20, d.Shared)      // derived setter wrote the BASE storage
        assertEquals("own", d.Own)      // base-declared property, derived receiver
        d.Own = "set"
        assertEquals("set", d.Own)
        assertEquals(20, (d as BfBase).Shared)
    }

    // A CUSTOM accessor over a backing field is still accessor-routed -> still renamed, and `field` inside the
    // accessor still addresses the renamed storage.
    @TestAttribute
    fun customAccessorsOverABackingFieldAreRenamedToo() {
        val fields = fieldNames(Type.GetType("BfCustomAccessor")!!)
        assertTrue(fields.contains("<Scaled>k__BackingField"))
        assertFalse(fields.contains("Scaled"))
        val c = BfCustomAccessor()
        c.Scaled = 3                    // setter: field = 3 * 2
        assertEquals(106, c.Scaled)     // getter: field + 100
    }

    // An `object` singleton's property is renamed; its `INSTANCE` static is NOT (no CLR property backs it). A rich
    // enum's user property is renamed; `__name`/`__ordinal` and the entry statics are NOT.
    @TestAttribute
    fun singletonAndRichEnumRenameOnlyThePropertyStorage() {
        val singleton = fieldNames(Type.GetType("BfSingleton")!!)
        assertTrue(singleton.contains("<Slot>k__BackingField"))
        assertTrue(singleton.contains("INSTANCE"))
        assertFalse(singleton.contains("Slot"))
        BfSingleton.Slot = 4
        assertEquals(4, BfSingleton.Slot)

        val rich = fieldNames(Type.GetType("BfRich")!!)
        assertTrue(rich.contains("<Weight>k__BackingField"))
        assertTrue(rich.contains("__name"))
        assertTrue(rich.contains("__ordinal"))
        assertFalse(rich.contains("Weight"))
        assertEquals(2, BfRich.B.Weight)
    }

    // The boundary: a `lateinit var` has no CLR property, so its field IS the user-visible member and keeps its name.
    @TestAttribute
    fun plainFieldBackedPropertiesKeepTheirName() {
        val t = Type.GetType("BfLateinitHolder")!!
        assertEquals(listOf("Late"), fieldNames(t))
        assertEquals(0, propertyNames(t).size)
    }
}
