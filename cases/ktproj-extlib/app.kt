// I2 + I4 — `Ext.Widget` lives in a referenced (non-BCL) assembly, injected façade-free via
// import-scan + the AssemblyResolver. Its .NET event `Changed` is subscribed with a Kotlin lambda.
import Ext.Widget
fun main() {
	val w = Widget("gadget")
	println("Add(2,3) = ${w.Add(2, 3)}")
	// Widget.Name is a .NET reference type from a NON-NRT (oblivious) assembly -> Kotlin PLATFORM type `String!`,
	// usable without null ceremony (and freely assignable to String?). Exercises the injector's ConeFlexibleType.
	val name: String = w.Name
	println("name: $name (len ${name.length})")
	w.Enabled = true                                 // assign a plain Boolean to a .NET `bool?` property (Nullable<bool>)
	println("enabled: ${w.Enabled}")
	w.Changed += { n -> println("changed: $n") }     // .NET event `+=` a Kotlin handler (ClrEvent<T> operator)
	w.Fire(5)
	w.Fire(9)
}
