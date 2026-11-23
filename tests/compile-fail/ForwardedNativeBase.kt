import ProjectedNativeBase.*

class Leaf<T> : Root(), IMark
class Slot<A> : NativeRoot<A>() where A : Root, A : IMark
class Factory<A> where A : Root, A : IMark { fun make() = Slot<A>() }
class Relay<A> where A : Root, A : IMark { fun factory() = Factory<A>() }

// The native requirement belongs to construction, even if make is never called.
fun main() { Relay<Leaf<*>>() }
