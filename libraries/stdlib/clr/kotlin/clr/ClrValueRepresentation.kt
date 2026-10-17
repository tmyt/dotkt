package kotlin.clr

// A Kotlin superclass can admit native CLR values that cannot inherit its runtime class.
// Ordinary value positions use storageType, while inheritance, construction and native
// declarations retain the class. Classifier and member adapters are exact stdlib bindings;
// neither the frontend nor the CIL emitter interprets them.
@Target(AnnotationTarget.CLASS)
internal annotation class ClrValueRepresentation(
    val storageType: String,
    val classifier: String,
    val classifierFactory: String,
)

@Target(AnnotationTarget.FUNCTION)
internal annotation class ClrValueMember(val adapter: String)
