// Phase 2 — generic .NET methods (façade-free) + injected generic indexer, in pure IL.
// `Unsafe.SizeOf<T>()` / `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` are pure, deterministic
// static generic methods: the backend resolves the open definition and MakeGenericMethod's it with the
// call's type arg (the CLR has reified generics — no erasure dance). `Collection<T>` gains a real `this[i]`
// indexer (get_Item / set_Item on the constructed type).
import clrgen.Unsafe
import clrgen.RuntimeHelpers
import clrgen.Collection

fun main() {
    println(Unsafe.SizeOf<Int>())                                     // 4
    println(Unsafe.SizeOf<Long>())                                    // 8
    println(Unsafe.SizeOf<Double>())                                  // 8
    println(RuntimeHelpers.IsReferenceOrContainsReferences<Int>())    // False (a primitive holds no references)
    println(RuntimeHelpers.IsReferenceOrContainsReferences<String>()) // True  (a reference type)

    val c = Collection<Int>()
    c.Add(10); c.Add(20); c.Add(30)
    println(c[1])        // 20  (get_Item)
    c[1] = 99            // set_Item
    println(c[1])        // 99
    println(c.Count)     // 3
}
