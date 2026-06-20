// No façade .kt anywhere — clrgen.* come from <KotlinClrType> injection in the .ktproj.
import clrgen.StringBuilder
import clrgen.Math

fun main() {
	val sb = StringBuilder()
	sb.Append("no-facade via <KotlinClrType>; abs(-5)=").Append(Math.Abs(-5))
	println(sb.ToString())
}
