// Migrated verify-roundtrip.sh section `roundtrip-customprop` (#103) — the library half.
// A field-backed property with a CUSTOM accessor (`val x = 41; get() = field + 1`) compiles to a backing
// FIELD PLUS a get_/set_<name> accessor carrying the custom body. Consumed cross-module the consumer must
// INVOKE the accessor, NOT touch the raw field — else the custom getter/setter is silently BYPASSED (the #103
// miscompile: `topProp get()=field+1` returned the raw 41 instead of 42). Covers TOP-LEVEL + companion +
// member field-backed props, and independent get/set customness.
package roundtrip.cprop

val topProp: Int = 41
    get() = field + 1               // custom getter -> 42, NOT the raw 41
var topVar: Int = 0
    set(value) { field = value + 5 } // custom setter: set(10) -> 15 (default getter reads the field)
var topGetVar: Int = 100
    get() = field - 1                // custom getter + DEFAULT setter: set(50) then read -> 49
class Host {
    val kProp: Int = 7
        get() = field + 100          // member field-backed val, custom getter -> 107
    var kVar: Int = 0
        set(value) { field = value * 2 } // member var, custom setter: set(3) -> 6
    companion object {
        val cProp: Int = 10
            get() = field * 2        // companion field-backed val, custom getter -> 20
    }
}
