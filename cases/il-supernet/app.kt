// issue #14 RESIDUAL, R2: a `super.<m>()` to a FACADEGEN-INJECTED .NET base class must be a NON-virtual `call` to the
// base slot. kotc emits `callInstance … virtual:false super:true` by the .NET-owner FQN identity; bir2cir's
// NetInteropBinding reshapes it to `clrInstance` and now PROPAGATES the `super` marker (was DROPPED); ilemit's
// EmitClrCall emits `call` (not `callvirt`) for the reference owner. Without it `super.Next()` callvirt-re-dispatches
// to THIS class's Next override -> infinite recursion. This exercises the NetInteropBinding path (R1/il-superobj
// exercises the MemberCallSubstitution @ClrTypeAlias path — distinct binders, same `super` contract).

import System.Random

class SeededRandom(seed: Int) : Random(seed) {
	override fun Next(): Int = super.Next() + 1000   // super -> System.Random::Next (non-virtual base slot)
}

fun main() {
	val r1 = SeededRandom(42); val r2 = SeededRandom(42)
	val v1 = r1.Next(); val v2 = r2.Next()
	println(v1 >= 1000)     // True  (super.Next() returned a non-negative base value + the 1000 offset; no recursion)
	println(v1 == v2)       // True  (same seed -> deterministic; the override ran on both)
}
