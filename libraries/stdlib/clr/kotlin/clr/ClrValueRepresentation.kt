package kotlin.clr

// A semantic classifier can admit values beyond its physical @ClrTypeAlias face.
// Source types survive in Kotlin metadata; classifiers and virtual slots are explicit
// stdlib bindings, never facts inferred by the frontend or CIL emitter.
// Classifier helpers receive a String with one character per outer-to-inner vector:
// 'o' for existing open System.Array storage, 'a' for exact array storage, empty for a scalar.
// An interface marker preserves implemented-interface membership independently of the value-slot alias.
// sourceClassifier binds an unchanged common declaration from a CLR-only carrier; empty binds this declaration.
@Target(AnnotationTarget.CLASS)
internal annotation class ClrValueRepresentation(
    val classifier: String,
    val classifierFactory: String,
    val componentClassifier: String,
    val interfaceMarker: String,
    val sourceClassifier: String,
)

@Target(AnnotationTarget.FUNCTION)
internal annotation class ClrValueMember(val adapter: String, val slot: String)
