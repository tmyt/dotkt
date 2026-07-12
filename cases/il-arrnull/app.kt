// #113: arrayOfNulls<T>(n) for a value-type T must allocate `Nullable<T>[]` (not a native `T[]`), because
// arrayOfNulls<T> returns `Array<T?>`. A native `int[]` would corrupt on `stelem Nullable<int>` AND fail the
// `copyOf() as Array<T?>` reified cast (InvalidCastException). Must work generally: Int/Long/Double/Char.
fun main() {
    val a = arrayOfNulls<Int>(3)
    a[0] = 5
    println(a[0])       // 5
    println(a[1])       // null
    println(a.size)     // 3

    // copyOf() -> nativeClone() as Array<T?> round-trip: only succeeds when `a` is a real Nullable<int>[].
    val c = a.copyOf()
    c[1] = 7
    println(c[0])       // 5
    println(c[1])       // 7
    println(a[1])       // null  (copy is independent)
    println(a.toList()) // [5, null, null]

    // General across value-type args.
    val la = arrayOfNulls<Long>(2)
    la[0] = 100L
    println(la[0])      // 100
    println(la[1])      // null

    val da = arrayOfNulls<Double>(2)
    da[1] = 2.5
    println(da[0])      // null
    println(da[1])      // 2.5

    val ca = arrayOfNulls<Char>(2)
    ca[0] = 'x'
    println(ca[0])      // x
    println(ca[1])      // null

    // Reference-type arg stays correct (Nullable-wrap is a no-op for reference elements).
    val sa = arrayOfNulls<String>(2)
    sa[0] = "hi"
    println(sa[0])      // hi
    println(sa[1])      // null
}
