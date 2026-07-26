// #228 — AUTO-PROPERTY BACKING-FIELD SHAPE. A Kotlin property with default accessors becomes a real CLR property; its
// storage must carry a compiler-generated, unspeakable metadata name (`<Value>k__BackingField`, the C# convention), not
// the property's own name. Emitting both under one name made the type unusable through reflection-driven .NET
// libraries: Newtonsoft groups candidate members by name, so `SerializeObject` on such a type silently produced `{}`
// and the round-trip back threw on the null constructor argument.
//
// A property whose storage IS the user-visible member (`lateinit var`, `const`, a delegated `<p>$delegate`, a
// companion/top-level static, the `@ClrField` opt-out) emits no CLR property and therefore keeps its plain name — the
// last test pins that boundary.
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

    // (c) The reported symptom: JSON.NET must serialize the properties and deserialize them back.
    @TestAttribute
    fun jsonNetSerializesAndDeserializesAnAutoPropertyType() {
        val original = BfCarrier("ada", 36)
        original.Value = 7
        val json = JsonConvert.SerializeObject(original)
        assertEquals("{\"Name\":\"ada\",\"Age\":36,\"Value\":7}", json)
        val back = JsonConvert.DeserializeObject(json, Type.GetType("BfCarrier")!!) as BfCarrier
        assertEquals("ada", back.Name)
        assertEquals(36, back.Age)
        assertEquals(7, back.Value)
    }

    // The boundary: a `lateinit var` has no CLR property, so its field IS the user-visible member and keeps its name.
    @TestAttribute
    fun plainFieldBackedPropertiesKeepTheirName() {
        val t = Type.GetType("BfLateinitHolder")!!
        assertEquals(listOf("Late"), fieldNames(t))
        assertEquals(0, propertyNames(t).size)
    }
}
