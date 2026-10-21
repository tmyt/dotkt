package roundtrip.kclassvalues

import kotlin.reflect.KAnnotatedElement
import kotlin.reflect.KCallable
import kotlin.reflect.KClass
import kotlin.reflect.KClassifier

annotation class ImportedTyped(val type: KClass<*>)
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
fun makeTyped(): ImportedTyped = ImportedTyped(String::class)
fun classIdentity(value: KClass<*>): KClass<*> = value
fun annotatedIdentity(value: KAnnotatedElement): KAnnotatedElement = value
fun <T : KClassifier> boundedIdentity(value: T): T = value
inline fun <reified T> matches(value: Any?): Boolean = value is T
