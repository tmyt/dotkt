// (5) aliased .NET import: `import X as Y` must inject the type AND bind the alias — the PSI import scan
// (kotc --scan-imports, ImportScan.kt) strips the alias to the canonical FQN for facadegen, and Kotlin's normal
// import machinery binds `SB` to the injected classifier. A regex scan silently dropped this form (feedback (5)).
import System.Text.StringBuilder as SB

fun main() {
    val sb = SB()
    sb.Append("hello")
    sb.Append(", ")
    sb.Append("alias")
    println(sb.ToString())
    println(sb.Length)
}
