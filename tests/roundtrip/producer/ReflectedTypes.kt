package roundtrip.reflectedtypes

import kotlin.reflect.KType
import kotlin.reflect.typeOf

class LibraryBox<T>
fun declaredBox() = typeOf<LibraryBox<String>>()
inline fun <reified T> defaultType(value: KType = typeOf<List<T>>()) = value
inline fun <reified T> reflectAfter(block: () -> Unit): KType { block(); return typeOf<T>() }
inline fun <T> declaredInCallback(block: (KType) -> KType) = block(typeOf<List<T>>())
fun <T> declaredInDefault(shape: KType = typeOf<List<T>>()) = shape

inline fun <reified T> reflected(): KType = typeOf<T>()
inline fun <reified T> reflectedList(): KType = reflected<List<T?>>()
inline fun <reified T> reflectedInLambda(): () -> KType = { typeOf<List<T?>>() }
