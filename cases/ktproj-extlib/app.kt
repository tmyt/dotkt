// I2 + I4 — `Ext.Widget` lives in a referenced (non-BCL) assembly, injected façade-free via
// import-scan + the AssemblyResolver. Its .NET event `Changed` is subscribed with a Kotlin lambda.
import Ext.Widget
fun main() {
	val w = Widget("gadget")
	println("Add(2,3) = ${w.Add(2, 3)}")
	w.Enabled = true                                 // assign a plain Boolean to a .NET `bool?` property (Nullable<bool>)
	println("enabled: ${w.Enabled}")
	w.add_Changed { n -> println("changed: $n") }   // .NET event += Kotlin handler
	w.Fire(5)
	w.Fire(9)
}
