import ProjectedNativeBase.*

class Leaf<T> : Root(), IMark
class Direct<A> where A : Root, A : IMark { fun make() = NativeRoot<A>() }

fun main() { Direct<Leaf<*>>() }
