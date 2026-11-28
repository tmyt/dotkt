import InheritedStackAdmission.IFactory

open class NarrowFactory { open fun <T> Make(): String = "base" }
class DerivedFactory : NarrowFactory(), IFactory
