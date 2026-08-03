// A Kotlin function type wider than any CLR delegate. System.Func/Action carry arities 0..16 and the DotKt stdlib
// defines KFunc/KAction for 17..22; each of those is a real pre-baked type in the stdlib, and Kotlin's function types
// are unbounded, so the family cannot simply be extended one more row. Whether a Kotlin type has a CLR representation
// at all is bir2cir's decision, so the refusal comes from there and names the arity — not from a deeper failure to
// resolve a type nobody defined. Nothing before bir2cir objects: the frontend's builtin provider synthesizes
// kotlin.FunctionN for arbitrary N, so this source resolves cleanly.
fun wide(f: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int =
    f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23)

fun main() {
    println(wide { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22, p23 -> p1 + p23 })
}
