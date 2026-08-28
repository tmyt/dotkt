// #354 — implementing-position twin of the foreign array crossing. The source-visible projected override cannot
// fill the CLR `Nullable<Int32>[,]` slot because its Kotlin physical image is the unrelated `object[,]`.
import fgn.IArrayMatrixSink

class MatrixSink : IArrayMatrixSink {
    override fun CountMatrix(xs: Array<Int?>): Int = xs.size
}

fun main() {
    println(MatrixSink())
}
