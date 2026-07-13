import System.Threading.ThreadLocal
import System.Runtime.CompilerServices.RuntimeHelpers

fun main() {
    val tf = ThreadLocal<String>({ "hi" })          // #143: generic value-factory ctor injects
    println(tf.Value)                                 // hi
    val te = ThreadLocal<String>()
    val v = te.Value                                  // #143: Value is a PLATFORM type (String!), null when unset
    println(v == null)                                // True — the == null is legal (not 'always false'), and true at runtime
    val o: Any = "x"
    println(RuntimeHelpers.GetHashCode(o) == RuntimeHelpers.GetHashCode(o))   // #143: static GetHashCode injects -> True
}
