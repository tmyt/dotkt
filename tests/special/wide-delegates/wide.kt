// >16-arg function values: System.Func/Action top out at 16 value parameters (Func`17 = 16 args +
// TResult), so these shapes bind the stdlib's canonical KFunc`18 / KAction`17 (#220). This structural
// source drives the adjacent run.sh through the real pipeline (kotc -> bir2cir -> ilemit); the script
// additionally asserts this assembly DEFINES no delegate of its own and that dll2klib restores
// `accept` with the full 17-arg Kotlin function type.
fun accept(cb: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int =
    cb(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

// Low-arity function types stay System.Func/Action even when their signature contains a composite
// open type. PersistedAssemblyBuilder cannot encode Func<Array<E>>.Invoke directly, so ilemit must
// solve that as a MemberRef/MethodSpec problem without changing the nominal delegate ABI.
class Box<E>(val value: E)

fun <E> pull(provider: () -> Array<E>): Array<E> = provider()

fun <E, R> applyBox(value: Box<E>, transform: (Box<E>) -> R): R = transform(value)

fun <E> visitBox(value: Box<E>, action: (Box<E>) -> Unit) {
    action(value)
}

fun main() {
    val f: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int =
        { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> p17 }
    val a: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Unit =
        { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> println(p17) }
    println(f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
    a(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
    println(accept(f))
    println(pull { arrayOf(23) }[0])
    println(applyBox(Box(29)) { it.value })
    visitBox(Box(31)) { println(it.value) }
}
