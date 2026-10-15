package roundtrip.callablemetadata

interface Constraint
class Payload : Constraint

@PublishedApi internal fun <T : Constraint> metadataHelper(value: T): T = value
inline fun <T : Constraint> metadataInline(value: T, block: (T) -> T): T = block(metadataHelper(value))
@PublishedApi internal val metadataProperty: Payload get() = Payload()
class Constructed @PublishedApi internal constructor(val value: Payload)
