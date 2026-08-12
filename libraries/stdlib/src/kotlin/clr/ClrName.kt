package kotlin.clr

// Gives a Kotlin function or one property accessor its exact CLR MethodDef name. bir2cir applies the name after
// physical owner/signature lowering and rejects any remaining duplicate.
@Target(
    AnnotationTarget.FUNCTION,
    AnnotationTarget.PROPERTY_GETTER,
    AnnotationTarget.PROPERTY_SETTER,
)
@Retention(AnnotationRetention.BINARY)
public annotation class ClrName(val name: String)
