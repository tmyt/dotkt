package p
interface C { fun go(x: Int) }
class Impl : C { override fun go(x: Int) { println(x) } }
var cur: C? = null
fun call(x: Int) { cur?.go(x) }
