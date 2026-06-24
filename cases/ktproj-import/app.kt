// C-2: import-driven .NET resolution — just `import System.X`, no <KotlinClrType>, no façade, no clrgen.
import System.Text.StringBuilder
import System.Math

fun main() {
    val sb = StringBuilder()
    sb.Append("dotkt ").Append("imports ").Append("just work: ").Append(Math.Max(40, 2))
    println(sb.ToString())   // dotkt imports just work: 40
}
