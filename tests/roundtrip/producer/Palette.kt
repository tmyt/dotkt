// Migrated verify-roundtrip.sh section `roundtrip-enum` (#105) — the library half.
// A BASIC (constants-only) enum class emits as a CLR value-type enum deriving System.Enum with NO ToString/
// GetHashCode/Equals of its own — it INHERITS them. Consumed cross-module, calls to those inherited slots must
// take the value-type receiver by address + constrained-callvirt the System.Object slot (bir2cir
// NetInteropBinding -> ilemit constrained-callvirt). The consumer asserts toString/==/hashCode round-trip.
package roundtrip.palette

enum class Color { RED, GREEN }
