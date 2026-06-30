// .NET interop via facadegen: `import System.X` injects a real BCL INSTANCE type façade-free (no @Clr facade).
// System.Text.StringBuilder resolves to the real BCL type; Append/ToString/Length route as direct .NET members.
import System.Text.StringBuilder

fun main() {
	val sb = StringBuilder()
	sb.Append("Hello").Append(", ").Append("CLR ").Append(42)
	println(sb.ToString())
	println("length = ${sb.Length}")
}
