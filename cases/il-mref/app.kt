// .NET method references: bound (`obj::m` -> delegate over the instance method) and unbound
// (`NetType::m` -> a lifted __mref(self, args) = self.m(args)).
import System.Text.StringBuilder

fun <T> apply1(f: (StringBuilder) -> T, sb: StringBuilder): T = f(sb)

fun main() {
    val sb = StringBuilder()
    sb.Append("hello world")
    val g: () -> String = sb::ToString          // bound .NET method ref
    println(g())                                 // hello world
    val cleared = apply1(StringBuilder::Clear, sb)   // unbound .NET method ref
    println(cleared.ToString().length)           // 0
}
