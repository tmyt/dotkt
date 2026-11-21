import ProjectedNativeBase.*

class Leaf<T> : Root(), IMark
class Slot<A> : NativeRoot<A>() where A : Root, A : IMark

fun main() { Slot<Leaf<*>>() }
