package roundtrip.nominallist

open class NominalMutableList<E>(private val elements: MutableList<E>) : MutableList<E> by elements
open class NominalReadOnlyList<E>(private val elements: List<E>) : List<E> by elements

fun nominalMutableValue(): Any = NominalMutableList(mutableListOf(7, 9))
fun nominalReadOnlyValue(): Any = NominalReadOnlyList(listOf(7, 9))
fun nominalListCast(value: Any): List<*> = value as List<*>
fun nominalMutableListCast(value: Any): MutableList<*> = value as MutableList<*>
inline fun <reified T> nominalListIs(value: Any?): Boolean = value is T
inline fun <reified T> nominalListSafe(value: Any?): T? = value as? T

