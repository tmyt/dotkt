// FIR injection: cross-.NET-type members (makeWidget -> Widget) and array members (int[]/string[]).
import P.Engine
import P.Widget
import P.Arr

fun main() {
    println(Engine().makeWidget().value())     // 42  (cross-type return resolves to the injected Widget)
    println(Arr.sumArr(Arr.range3()))          // 60  (array param + array return)
    println(Arr.words().size)                  // 3   (string[] -> Array<String>)
    println(Arr.range3()[1])                   // 20  (array return is indexable)
}
