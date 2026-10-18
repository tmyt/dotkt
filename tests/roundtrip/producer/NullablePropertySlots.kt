package nullablepropertyslots

interface Source<out T> { val item: T }
interface DerivedSource<out T> : Source<T>
