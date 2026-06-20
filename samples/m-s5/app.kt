// S5 (generalized) — façade-free .NET interop. None of these types has a hand-written/generated
// façade .kt: `clrgen.*` are synthesized straight into FIR from metadata that facadegen produced
// by reflecting over the real System.Math / System.Console / System.Text.StringBuilder.
import clrgen.Math
import clrgen.Console
import clrgen.StringBuilder

fun main() {
	Console.WriteLine("abs(-9) = ${Math.Abs(-9)}")        // static method + overload resolution
	Console.WriteLine("max(3, 7) = ${Math.Max(3, 7)}")
	val sb = StringBuilder()                                // instance class + constructor
	sb.Append("Hello")                                     // self-returning instance method
	sb.Append(", CLR")
	Console.WriteLine("sb.Length = ${sb.Length}")          // .NET property
	Console.WriteLine("sb = ${sb.ToString()}")
}
