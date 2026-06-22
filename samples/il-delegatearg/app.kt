// A lambda passed to a .NET CONSTRUCTOR and a .NET METHOD whose parameter is a delegate type. The façade erases
// the delegate param (to object/func), so ilemit must recover the real delegate type from the ctor/method and
// build that specific delegate. (Was: EmitClrNew NullReferenceException; cf. `new Thread(lambda)`.)
import Kfc.Box
fun main() {
    val b = Box({ x -> x + 1 })   // delegate as ctor arg
    println(b.Apply(41))          // 42
    println(b.Run({ x -> x * 2 })) // delegate as method arg -> g(10)=20
    val c = Box({ x -> x * x })
    println(c.Apply(9))           // 81
}
