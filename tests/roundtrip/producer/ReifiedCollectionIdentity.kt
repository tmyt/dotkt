package roundtrip.collectionfamily

inline fun <reified T> collectionFamilyIs(value: Any?): Boolean = value is T
inline fun <reified T> collectionFamilySafe(value: Any?): T? = value as? T
inline fun <reified T> collectionFamilyCast(value: Any?): T = value as T
