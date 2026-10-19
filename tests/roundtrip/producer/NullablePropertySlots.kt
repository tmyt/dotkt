package nullablepropertyslots

interface Source<out T> { val item: T }
interface DerivedSource<out T> : Source<T>
interface MutableSource<T> { var item: T }
abstract class BaseSource<T> { abstract val item: T }
