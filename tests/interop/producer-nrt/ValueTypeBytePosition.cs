// Producer source for the NRT byte-POSITION arm of the value-type walk. Compiled with C# NRT ENABLED (this csproj
// sets <Nullable>enable</Nullable>), so csc writes the flattened [Nullable] array these signatures depend on:
//   Dictionary<Grade, string?> -> [Nullable(1,2)] — Dictionary(1), the BARE enum holds NO byte, string?(2)
//   Dictionary<Cell, string?>  -> [Nullable(1,2)] — the same with a bare struct
// A walk that gives the bare value type a byte of its own reads the `2` as the KEY's annotation (projecting
// `Grade?`, i.e. Nullable<Grade>) and leaves the value to the declaration's context byte, so the projected
// parameter stops accepting `Dictionary<Grade, String?>` and the Kotlin call no longer compiles.
//
// It has to be produced here rather than taken from the BCL: a scan of the whole net10.0 reference pack for a
// public signature putting a bare non-primitive value type AHEAD of a reference node in one slot finds exactly one
// (a System.Text.Json delegate), which no test references. The BCL arms of the same walk — a value type must not be
// annotated, and its sibling overload must stay reachable — are pinned against `String.Compare` in
// ../consumer/fixtures/BclValueTypeArgumentTests.kt. Own namespace (NrtPos).
using System.Collections.Generic;
namespace NrtPos {
    public enum Grade { Low, High }
    public struct Cell {
        public int Row;
        public int Col;
        public Cell(int row, int col) { Row = row; Col = col; }
    }
    public static class Api {
        // A bare ENUM ahead of the nullable value: reachable from Kotlin only as Dictionary<Grade, String?>.
        public static string Describe(Dictionary<Grade, string?> map) =>
            (map[Grade.Low] ?? "<null>") + "/" + (map[Grade.High] ?? "<null>");
        // The same with a bare STRUCT key, so the arm does not rest on enums alone.
        public static string DescribeCells(Dictionary<Cell, string?> map) =>
            (map[new Cell(0, 0)] ?? "<null>") + "/" + (map[new Cell(1, 1)] ?? "<null>");
    }
}
