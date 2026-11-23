package roundtrip.kclassvalues

import kotlin.reflect.KAnnotatedElement
import kotlin.reflect.KCallable
import kotlin.reflect.KClass
import kotlin.reflect.KClassifier

annotation class ImportedTyped(val type: KClass<*> = String::class)
annotation class CollectionKey(val type: KClass<*> = Map::class)
fun defaultCollectionKey(key: KClass<*> = Map::class): KClass<*> = key
inline fun <reified T : Any> defaultReifiedKey(key: KClass<*> = T::class): KClass<*> = key
inline fun <reified T> classifierAfter(block: () -> Unit): String? { block(); return T::class.simpleName }
open class OrdinaryBase(val payload: String)
open class ImportedManual : OrdinaryBase("base"), KClass<Any> {
    override val simpleName: String get() = "Imported"
    override val qualifiedName: String get() = "roundtrip.Imported"
    override val annotations: List<Annotation> get() = emptyList()
    override val members: Collection<KCallable<*>> get() = emptyList()
    override fun isInstance(value: Any?): Boolean = value is Int
    override fun equals(other: Any?): Boolean = this === other
    override fun hashCode(): Int = 29
}
@ImportedTyped
open class ImportedPlainBase<T>(val expected: T) {
    override fun equals(other: Any?): Boolean = this === other
    override fun hashCode(): Int = 43
    open val simpleName: String? get() = "ImportedPlain"
    open val qualifiedName: String? get() = "roundtrip.ImportedPlain"
    open val annotations: List<Annotation> get() = emptyList()
    open val members: Collection<KCallable<*>> get() = emptyList()
    open fun isInstance(value: Any?): Boolean = value == expected
}
interface ImportedDefault : KClass<Any> {
    override val simpleName: String get() = "ImportedDefault"
    override val qualifiedName: String get() = "roundtrip.ImportedDefault"
    override val annotations: List<Annotation> get() = emptyList()
    override val members: Collection<KCallable<*>> get() = emptyList()
    override fun isInstance(value: Any?): Boolean = value is Long
}
open class ImportedToken<Noise, Value : Any>(value: Noise) : ImportedPlainBase<Noise>(value), KClass<Value>
abstract class ClassLookup {
    abstract fun <T : Any> typeName(key: KClass<T>): String
    abstract fun <T : Any> presentName(key: KClass<T>?): String
    abstract fun <T : Any> emptyValues(key: KClass<T>): Array<T?>
}
fun capturedName(lookup: ClassLookup, key: KClass<*>): String = lookup.typeName(key)
fun makeTyped(): ImportedTyped = ImportedTyped(String::class)
fun classIdentity(value: KClass<*>): KClass<*> = value
fun annotatedIdentity(value: KAnnotatedElement): KAnnotatedElement = value
fun <T : KClassifier> boundedIdentity(value: T): T = value
inline fun <reified T> matches(value: Any?): Boolean = value is T
inline fun <reified T : Any> reflectedLiteral(): KClass<T> = T::class
