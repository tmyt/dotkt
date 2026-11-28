import InheritedStackAdmission.IFactory

open class NarrowFactory { open fun <S, U> Make(): Array<U?> = error("not called") }
class DerivedFactory : NarrowFactory(), IFactory
