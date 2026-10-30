@file:OptIn(kotlin.uuid.ExperimentalUuidApi::class)

package roundtrip.uuidentropy

import kotlin.time.Instant
import kotlin.uuid.Uuid

fun randomVersionFour(): Uuid = Uuid.generateV4()
fun randomVersionSeven(): Uuid = Uuid.generateV7()
fun versionSevenAt(millis: Long): Uuid = Uuid.generateV7NonMonotonicAt(Instant.fromEpochMilliseconds(millis))
fun savedUuidFactory(): () -> Uuid = Uuid::generateV4
inline fun observeUuid(block: (Uuid) -> String): String = block(Uuid.generateV4())
