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
import EnumLiteralInterop.EnumOrderApi
import EnumLiteralInterop.OrderedU32
import EnumLiteralInterop.OrderedU64
import EnumLiteralInterop.OrderedI64
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Environment

private typealias ImportedCodeAlias = Code

private fun importedLiteralDefault(value: Code = Code.Negative): Int = EnumLiteralApi.Receive(value)
private fun <T : Enum<T>> importedOrderOrdinal(value: T): Int = value.ordinal
private fun <T : Comparable<T>> importedOrderCompare(left: T, right: T): Int = left.compareTo(right)
inline fun <reified T : Enum<T>> importedOrderValues(): Array<T> = enumValues<T>()

@kotlin.clr.ClrEnum
private enum class ImportedOrderKotlinControl(value: Int) { FIRST(10), SECOND(-4), LAST(0) }

class ImportedEnumLiteralTests {
    @TestAttribute
    fun nativeOrderUsesProjectedDeclarationsWithoutChangingAliasIdentity() {
        val negative = EnumOrderApi.Negative()
        val sparse = EnumOrderApi.Sparse()
        val alias = EnumOrderApi.Alias()
        assertEquals(-7, EnumLiteralApi.ReceiveTop(negative))
        assertEquals(1, negative.ordinal)
        assertEquals(1, importedOrderOrdinal(negative))
        assertEquals(-1, sparse.compareTo(negative))
        assertEquals(-1, importedOrderCompare(sparse, negative))
        assertEquals(1, importedOrderCompare(negative, sparse))
        assertEquals(sparse, alias)
        assertEquals(0, alias.ordinal)
        assertEquals(0, importedOrderOrdinal(alias))
        assertEquals(0, importedOrderCompare(sparse, alias))
        val expected = "103:-7:103"
        assertEquals(expected, TopCode.values().joinToString(":") { EnumLiteralApi.ReceiveTop(it).toString() })
        assertEquals(expected, enumValues<TopCode>().joinToString(":") { EnumLiteralApi.ReceiveTop(it).toString() })
        val factory: () -> Array<TopCode> = ::importedOrderValues
        assertEquals(expected, factory().joinToString(":") { EnumLiteralApi.ReceiveTop(it).toString() })
        val kotlinFactory: () -> Array<ImportedOrderKotlinControl> = ::importedOrderValues
        assertEquals("FIRST:SECOND:LAST", kotlinFactory().joinToString(":") { it.name })
        assertEquals(1, importedOrderOrdinal(ImportedOrderKotlinControl.SECOND))
        assertEquals(-2, importedOrderCompare(ImportedOrderKotlinControl.FIRST, ImportedOrderKotlinControl.LAST))
        assertEquals("0:1:0", enumValues<TopCode>().joinToString(":") { importedOrderOrdinal(it).toString() })
    }

    @TestAttribute
    fun nativeOrderRetainsSignedAndUnsignedHighBits() {
        val u32 = EnumOrderApi.U32High()
        val u64 = EnumOrderApi.U64High()
        val i64 = EnumOrderApi.I64High()
        assertEquals("4294967295", EnumOrderApi.U32Bits(u32))
        assertEquals("18446744073709551615", EnumOrderApi.U64Bits(u64))
        assertEquals("-9223372036854775808", EnumOrderApi.I64Bits(i64))
        assertEquals(0, u32.ordinal)
        assertEquals(0, u64.ordinal)
        assertEquals(0, i64.ordinal)
        assertEquals(0, importedOrderOrdinal(u32))
        assertEquals(0, importedOrderOrdinal(u64))
        assertEquals(0, importedOrderOrdinal(i64))
        assertEquals(-1, importedOrderCompare(u32, OrderedU32.Zero))
        assertEquals(-1, importedOrderCompare(u64, OrderedU64.Zero))
        assertEquals(-1, importedOrderCompare(i64, OrderedI64.Zero))
        assertEquals("4294967295:0:70001:4294967295", enumValues<OrderedU32>().joinToString(":") { EnumOrderApi.U32Bits(it) })
        assertEquals("18446744073709551615:0:9000000000:18446744073709551615", enumValues<OrderedU64>().joinToString(":") { EnumOrderApi.U64Bits(it) })
        assertEquals("-9223372036854775808:0:9223372036854775807", enumValues<OrderedI64>().joinToString(":") { EnumOrderApi.I64Bits(it) })
        assertEquals("4294967295:0:70001:4294967295", listOf(OrderedU32.High, OrderedU32.Zero, OrderedU32.Sparse, OrderedU32.Alias).joinToString(":") { EnumOrderApi.U32Bits(it) })
        assertEquals("18446744073709551615:0:9000000000:18446744073709551615", listOf(OrderedU64.High, OrderedU64.Zero, OrderedU64.Sparse, OrderedU64.Alias).joinToString(":") { EnumOrderApi.U64Bits(it) })
        assertEquals("-9223372036854775808:0:9223372036854775807", listOf(OrderedI64.High, OrderedI64.Zero, OrderedI64.Sparse).joinToString(":") { EnumOrderApi.I64Bits(it) })
    }

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
