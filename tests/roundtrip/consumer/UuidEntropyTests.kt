package roundtriptests.uuidentropy

import NUnit.Framework.TestAttribute
import kotlin.uuid.Uuid
import roundtrip.uuidentropy.*

private fun checkImportedUuid(value: Uuid, version: Int) {
    val bytes = value.toByteArray()
    check((bytes[6].toInt() and 0xf0) == (version shl 4))
    check((bytes[8].toInt() and 0xc0) == 0x80)
    check(Uuid.fromByteArray(bytes) == value)
    check(Uuid.parse(value.toHexDashString()) == value)
}

class UuidEntropyTests {
    @TestAttribute fun libraryGeneratorsAndSavedFactoryRetainUuidContracts() {
        checkImportedUuid(randomVersionFour(), 4)
        checkImportedUuid(savedUuidFactory()(), 4)
        checkImportedUuid(randomVersionSeven(), 7)
        val value = versionSevenAt(1_735_689_600_123L)
        checkImportedUuid(value, 7)
        check(value.toLongs { most, _ -> most ushr 16 } == 1_735_689_600_123L)
    }

    @TestAttribute fun importedInlineGeneratorPassesOneCompleteUuidToTheCallback() {
        var calls = 0
        var captured: Uuid? = null
        val text = observeUuid {
            calls++
            captured = it
            checkImportedUuid(it, 4)
            it.toHexDashString()
        }
        check(calls == 1)
        check(Uuid.parse(text) == captured)
    }
}
