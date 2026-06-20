// IL parity: `as?` safe cast — value type (-> T?) and reference type (-> isinst).
fun describe(x: Any): String {
	val n = x as? Int
	return if (n != null) "int:$n" else "other"
}
fun asStr(x: Any): String {
	val s = x as? String
	return s ?: "none"
}
fun main() {
	println(describe(42))
	println(describe("hi"))
	println(asStr("yo"))
	println(asStr(7))
}
