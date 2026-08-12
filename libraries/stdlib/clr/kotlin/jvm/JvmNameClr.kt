package kotlin.jvm

import kotlin.annotation.AnnotationTarget.FILE
import kotlin.annotation.AnnotationTarget.FUNCTION
import kotlin.annotation.AnnotationTarget.PROPERTY_GETTER
import kotlin.annotation.AnnotationTarget.PROPERTY_SETTER

// CLR compatibility actual for the common optional expectation. kotc recognizes this function/accessor annotation
// as an alias of kotlin.clr.ClrName; the FILE target remains metadata-only and does not rename CLR file facades.
@Target(FILE, FUNCTION, PROPERTY_GETTER, PROPERTY_SETTER)
@Retention(AnnotationRetention.BINARY)
public actual annotation class JvmName(actual val name: String)
