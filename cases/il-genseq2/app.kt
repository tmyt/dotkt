// C13a: a generic capturing closure passed as a DELEGATE argument (generateSequence's `{ seed }` -> the
// GeneratorSequence ctor's Function0 param). ilemit's delegate-arg binding path used to emit the generic
// closure's newobj with an OPEN generic operand (Closure`1::.ctor(!0)) -> TypeLoadException; and the
// GeneratorSequence iterator's delegateInvoke passed a boxed T? to a `Func<T,object>::Invoke(!0)` slot with
// no unbox -> InvalidProgramException at a VALUE-type element. Both fixed: a value element and a reference
// element drive the cold sequence correctly.
fun main() {
    println(generateSequence(1){ it * 2 }.take(3).toList())        // [1, 2, 4]
    println(generateSequence("a"){ it + "b" }.take(3).toList())    // [a, ab, abb]
    println(generateSequence(3){ it + 1 }.take(4).sum())           // 3+4+5+6 = 18
}
