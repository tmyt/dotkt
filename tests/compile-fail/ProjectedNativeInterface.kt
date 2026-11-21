import ProjectedNativeBase.*

class Leaf<T> : Root(), IMark
class Slot<A> : INative<A> where A : Root, A : IMark

fun main() { Slot<Leaf<*>>() }
