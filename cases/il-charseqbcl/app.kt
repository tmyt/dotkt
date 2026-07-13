// #148: a NON-literal (computed / BCL-origin) String flowing as the receiver of a stdlib CharSequence
// extension (split/replace/substring) must be adapter-wrapped by bir2cir. Before #148 only const/local/param
// receivers were wrapped, so a property-read / app-fun-result / `!!` / BCL-call-result String reached the
// `dotkt$CharSequence` slot RAW and the extension's `subSequence`/`get_length` interface call hit the
// body-less synthetic -> EntryPointNotFoundException at runtime (silently un-gated; the #92 residual).
class Cfg(val body: String)
fun lines(): String = "a\nb\nc"
fun word(): String = "hello"
fun main() {
    for (p in lines().split("\n")) println("f:$p")   // app-fun-result receiver (owner:null callStatic, ret-less)
    val c = Cfg("k1\nk2")
    for (p in c.body.split("\n")) println("p:$p")     // property-getter receiver (callInstance get_body, ret-less)
    val m = mapOf("x" to "1\n2")
    for (p in m["x"]!!.split("\n")) println("m:$p")   // `!!` + map-indexer receiver (valueBlock, String?-typed)
    val sb = StringBuilder(); sb.append("u\nv")
    println(sb.toString().replace("\n", "-"))         // BCL-origin (System.Text.StringBuilder.ToString) receiver
    println(word().substring(1, 4))                   // app-fun-result receiver into substring
    println(c.body.replace("\n", "+"))                // property-getter receiver into replace
}
