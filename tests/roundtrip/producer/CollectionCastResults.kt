package collection.cast.results

fun checkedCollection(value: Any): Collection<Any> = value as Collection<Any>
fun safeCollection(value: Any): Collection<Any>? = value as? Collection<Any>
fun checkedSet(value: Any): Set<Any> = value as Set<Any>
fun safeSet(value: Any): Set<Any>? = value as? Set<Any>
fun checkedMutableSet(value: Any): MutableSet<Any> = value as MutableSet<Any>
fun safeMutableSet(value: Any): MutableSet<Any>? = value as? MutableSet<Any>
fun checkedExistentialCollection(value: Any): Collection<*> = value as Collection<*>
fun safeExistentialCollection(value: Any): Collection<*>? = value as? Collection<*>
