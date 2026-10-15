import EnumLiteralInterop.Entries.Code
import EnumLiteralInterop.Entries.I8
import EnumLiteralInterop.Entries.U8
import EnumLiteralInterop.Entries.I16
import EnumLiteralInterop.Entries.U16
import EnumLiteralInterop.Entries.I32
import EnumLiteralInterop.Entries.U32
import EnumLiteralInterop.Entries.I64
import EnumLiteralInterop.Entries.U64
import EnumLiteralInterop.Entries.Mask
import EnumLiteralInterop.EnumLiteralApi
import EnumLiteralInterop.TopCode
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Environment

private typealias ImportedCodeAlias = Code

private fun importedLiteralDefault(value: Code = Code.Negative): Int = EnumLiteralApi.Receive(value)

class ImportedEnumLiteralTests {
    @TestAttribute
    fun sparseNegativeAndAliasedEntriesRemainNominalEnumValues() {
        assertEquals(73, EnumLiteralApi.Receive(Code.Sparse))
        assertEquals(-19, EnumLiteralApi.Receive(Code.Negative))
        assertEquals(0, EnumLiteralApi.Receive(Code.Zero))
        assertEquals(73, EnumLiteralApi.Receive(Code.Alias))
        val stored: ImportedCodeAlias = ImportedCodeAlias.Alias
        assertEquals(73, EnumLiteralApi.Receive(stored))
        assertEquals(-19, importedLiteralDefault())
        assertEquals(Code.Sparse, stored)
        assertEquals(103, EnumLiteralApi.ReceiveTop(TopCode.Sparse))
        val top = TopCode.Negative
        assertEquals(-7, EnumLiteralApi.ReceiveTop(top))
        assertEquals(103, EnumLiteralApi.ReceiveTop(TopCode.Alias))

        val flags = Mask.High or Mask.AliasLow
        assertEquals(130, EnumLiteralApi.ReceiveMask(flags))
        assertEquals(true, Mask.Low in flags)
    }

    @TestAttribute
    fun everyNativeUnderlyingRepresentationSurvivesDirectAndStoredEntries() {
        val expected = "-128:255:-32768:65535:-2147483648:4294967295:-9223372036854775808:18446744073709551615"
        assertEquals(expected, EnumLiteralApi.Widths(I8.High, U8.High, I16.High, U16.High, I32.High, U32.High, I64.High, U64.High))
        val i8 = I8.High
        val u8 = U8.High
        val i16 = I16.High
        val u16 = U16.High
        val i32 = I32.High
        val u32 = U32.High
        val i64 = I64.High
        val u64 = U64.High
        assertEquals(expected, EnumLiteralApi.Widths(i8, u8, i16, u16, i32, u32, i64, u64))
        assertEquals("17:17:317:317:70001:70001:9000000000:9000000000",
            EnumLiteralApi.Widths(I8.Sparse, U8.Sparse, I16.Sparse, U16.Sparse, I32.Sparse, U32.Sparse, I64.Sparse, U64.Sparse))
    }

    @TestAttribute
    fun frameworkNestedEntryMatchesTheSameTargetNativeControl() {
        assertEquals(40, EnumLiteralApi.ReceiveProfile(Environment.SpecialFolder.UserProfile))
        val stored = Environment.SpecialFolder.UserProfile
        assertEquals(40, EnumLiteralApi.ReceiveProfile(stored))
        assertEquals(EnumLiteralApi.ProfileControl(), Environment.GetFolderPath(stored))
    }
}
