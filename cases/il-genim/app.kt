// Generic interface method: call Convert<T> through the IConv interface type, and pass a Conv where IConv is
// expected (the implementing class is assignable to the interface). fir2ir fake-overrides the generic method.
import P.Conv
import P.IConv
fun viaIface(c: IConv): String = c.Convert<String>("hello")
fun main() {
    val c = Conv()
    println(viaIface(c))                 // hello — through interface type
    println(c.Convert<String>("world"))  // world — Conv assignable to IConv usage
}
