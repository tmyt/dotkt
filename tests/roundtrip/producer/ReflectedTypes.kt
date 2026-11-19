package roundtrip.reflectedtypes

import kotlin.reflect.KType
import kotlin.reflect.typeOf

inline fun <reified T> reflected(): KType = typeOf<T>()
inline fun <reified T> reflectedList(): KType = reflected<List<T?>>()
inline fun <reified T> reflectedInLambda(): () -> KType = { typeOf<List<T?>>() }
