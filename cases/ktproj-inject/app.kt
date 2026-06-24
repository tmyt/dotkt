// No façade .kt anywhere — the .NET types are resolved by scanning the `import System.X` lines.
import System.Text.StringBuilder
import System.Math

fun main() {
	val sb = StringBuilder()
	sb.Append("no-facade via import scan; abs(-5)=").Append(Math.Abs(-5))
	println(sb.ToString())
}
