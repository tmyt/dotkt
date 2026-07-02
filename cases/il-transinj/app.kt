// (3) generic-typed members + (6) transitive injection, façade-free:
//   - IList<Widget> / IReadOnlyList<Widget> / Dictionary<String,Widget> / IEnumerable<String>
//     in MEMBER-SIGNATURE position resolve as real constructed generics (not Any?).
//   - Gadget (hop 1) and Sprocket (hop 2) are NEVER imported — the facadegen reachable-closure
//     injects them because they appear in Widget.Make() / Gadget.Core() signatures.
import TX.Panel
import TX.Widget

fun main() {
    val panel = Panel()
    val w = Widget("w1")
    panel.Children.Add(w)
    println(panel.Children.Count)
    println(panel.Children[0].Name)
    println(panel.View.Count)
    println(panel.View[0].Name)
    println(w.Make().Tag)
    println(w.Make().Core().Size)
    panel.Index.Add("k", w)
    println(panel.Index["k"].Name)
    for (n in panel.Names()) println(n)
}
