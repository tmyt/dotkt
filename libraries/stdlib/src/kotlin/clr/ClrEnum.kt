package kotlin.clr

/**
 * Publishes a constants-only Kotlin enum as a native CLR enum with explicit integral values.
 *
 * The enum must declare exactly one non-property primary-constructor parameter of type [Byte], [UByte],
 * [Short], [UShort], [Int], [UInt], [Long], or [ULong]. Every entry supplies one distinct compile-time constant;
 * the parameter is compiler vocabulary and creates no runtime constructor, field, or property.
 */
@Target(AnnotationTarget.CLASS)
@Retention(AnnotationRetention.BINARY)
public annotation class ClrEnum
