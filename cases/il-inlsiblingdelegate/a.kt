// F4 (#63) file A: nests B's `bPick` inside a §4.4ii MATERIALIZED carrier. `wrap` is inline; its crossinline
// param `t` is invoked inside `{ t(x) }`, a lambda passed to the NON-inline `callIt` -> the carrier must be
// materialized into a real newClosure delegate. The nested `bPick(...)` splices FIRST (post-order), depositing
// B's `newDelegate` (__lambda in B's file class) INTO the carrier body. Before F4, `_appLocalMethods` held only
// A's root-file methods -> B's __lambda judged non-app-local -> HasUnmaterializableNested refused -> fail-loud.
// With MODULE-WIDE collection it is app-local -> the carrier materializes; ilemit ldftn-resolves B's __lambda.
fun callIt(g: () -> Int): Int = g()
inline fun wrap(x: Int, crossinline t: (Int) -> Int): Int = callIt { t(x) }

fun main() {
    // wrap(20){ bPick(false){5} }: carrier { t(20) } materialized; t = { bPick(false){5} };
    //   bPick(false){5} = bSink(7){ it + 100 } = 7 + 100 = 107
    println(wrap(20) { bPick(false) { 5 } })   // 107
    // cond=true -> primary()=5 (the else-branch newDelegate is STILL present in the spliced body -> still materialized)
    println(wrap(10) { bPick(true) { 5 } })    // 5
}
