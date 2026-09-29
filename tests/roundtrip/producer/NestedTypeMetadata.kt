package nestedmetadata

class Outer<O>(val outerValue: O) {
    inner class Item<I>(val value: I) {
        fun outer(): O = outerValue
    }
}

fun readInner(item: Outer<*>.Item<String>): String = item.value
fun readOuter(item: Outer<Int>.Item<*>): Int = item.outer()
fun readNullable(item: Outer<*>.Item<String>?): String? = item?.value
fun <T> echoInner(item: Outer<*>.Item<T>): Outer<*>.Item<T> = item

class Nest<A>(val a: A) {
    inner class Middle<B>(val b: B) {
        inner class Leaf<C>(val c: C) {
            fun middle(): B = b
            fun outer(): A = a
        }
    }
}

fun readLeaf(leaf: Nest<*>.Middle<String>.Leaf<Int>): Int = leaf.c + leaf.middle().length
fun readLeafOuter(leaf: Nest<Boolean>.Middle<*>.Leaf<*>): Boolean = leaf.outer()
