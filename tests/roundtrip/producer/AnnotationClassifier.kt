package roundtrip.annotationclassifier

annotation class ImportedMarker(val label: String)
open class OrdinaryBase(val label: String)
class ImportedManual : OrdinaryBase("manual"), Annotation
interface ExtendedAnnotation : Annotation
class ImportedExtended : OrdinaryBase("extended"), ExtendedAnnotation
fun annotationIdentity(value: Annotation): Annotation = value
fun makeAnnotation(): Annotation = ImportedMarker("factory")
fun annotationMembership(value: Any?): Boolean = value is Annotation
fun <T : Annotation> boundedIdentity(value: T): T = value
inline fun <reified T> reifiedMembership(value: Any?): Boolean = value is T
