// C-1: read a .NET static field/const (System.Math.PI), façade-free.
import clrgen.Math
fun main() {
	println(Math.PI > 3.0)
	println(Math.PI < 3.2)
}
