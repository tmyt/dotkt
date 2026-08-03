// #220 — a SECOND producer declaring the same arity-17 shape as roundtrip.wide, so the consumer can prove the two
// modules name ONE delegate type. It lives in the MPP producer purely because that is the other assembly the
// round-trip consumer already references; nothing here is multiplatform-specific. The `mpp` name prefixes keep the
// bare function names distinct from the other producer's: a same-named PARAMETERLESS top-level function declared by
// two referenced modules is resolved arbitrarily today, which is a separate defect and not what these tests pin.
package roundtrip.wide.mpp

fun mppParam17(f: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int = f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

fun mppRet17(): (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int = { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> p1 * 100 + p17 }
