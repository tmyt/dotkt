// Multi-file synthetic-type collision regression: two files each emit a capturing closure (`<>dotkt_*_Closure*`) and a
// ref cell for a captured `var` (`<>dotkt_*_Ref_bool`). With per-file synthetic names these would collide in the one
// linked assembly (orphaned TypeBuilder -> Save crash). BirEmitter now prefixes synthetics with the file class.
fun applyA(f: () -> Int): Int = f()
fun fromA(): Int { var flag = false; return applyA({ flag = true; if (flag) 10 else 0 }) }
fun main() { println(fromA()); println(fromB()) }
