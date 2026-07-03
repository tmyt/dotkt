// An object expression / SAM that CAPTURES the enclosing generic type parameter `T`. On the CLR generics are
// reified, so the flattened top-level class must itself be generic over T and be instantiated with the enclosing
// arg at the `new` site (the closure/SAM path already does this; this exercises the object-literal path).
interface Box<T> { fun get(): T }

fun <T> boxed(v: T): Box<T> = object : Box<T> {
    override fun get(): T = v
}

// A fun-interface (SAM) capturing T — the shape the stdlib's `Sequence { ... }` / `Iterable { ... }` builders use.
fun interface Producer<T> { fun make(): T }
fun <T> produce(v: T): Producer<T> = Producer { v }

fun main() {
    println(boxed(42).get())          // 42
    println(boxed("hi").get())        // hi
    println(produce(7).make())        // 7
    println(produce("ok").make())     // ok
}
