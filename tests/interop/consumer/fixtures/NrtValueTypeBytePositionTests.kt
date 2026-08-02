// The NRT byte POSITION a .NET value type occupies, consumed from the NRT-ENABLED C# producer (../producer-nrt,
// namespace NrtPos).
//
// `Dictionary<Grade, string?>` is `[Nullable(1,2)]`: the Dictionary holds the 1, the BARE enum holds NO byte at all,
// and the `2` belongs to the string. A walk that gives the bare value type a byte reads that `2` as the KEY's
// annotation and leaves the value to the declaration's context byte — the parameter projects as
// `Dictionary<Grade?, String>`, and this file stops compiling. That is the arm the value-type rule exists for, and
// it is separate from the ANNOTATION arm (a value type must never become nullable) that
// BclValueTypeArgumentTests.kt pins against `String.Compare`: the enum there is a whole parameter of its own, so
// nothing follows it in its slot to shift.
//
// The shape is produced rather than borrowed because the BCL does not offer it: a scan of the net10.0 reference pack
// for a public signature putting a bare non-primitive value type ahead of a reference node in one slot yields a
// single System.Text.Json delegate. An enum key and a struct key are asserted together — they are one predicate
// (`_valueTypeNames`, seeded from every `System.ValueType`/`System.Enum` definition), so neither can regress alone.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Collections.Generic.Dictionary
import NrtPos.Api
import NrtPos.Cell
import NrtPos.Grade

class NrtValueTypeBytePositionTests {
    // `Dictionary<Grade, String?>` binds the parameter only when the enum key stayed non-nullable AND the `2` landed
    // on the value; the nulls prove the value type argument really is nullable rather than merely accepted.
    @TestAttribute
    fun bareValueTypeAheadOfANullableReference() {
        val byGrade = Dictionary<Grade, String?>()
        byGrade[Grade.Low] = null
        byGrade[Grade.High] = "x"
        assertEquals("<null>/x", Api.Describe(byGrade))

        val byCell = Dictionary<Cell, String?>()
        byCell[Cell(0, 0)] = null
        byCell[Cell(1, 1)] = "y"
        assertEquals("<null>/y", Api.DescribeCells(byCell))
    }
}
