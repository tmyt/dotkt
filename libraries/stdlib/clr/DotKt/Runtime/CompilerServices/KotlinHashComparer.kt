package DotKt.Runtime.CompilerServices

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IEqualityComparer")
internal interface NativeHashComparer<T> {
    @kotlin.clr.ClrIntrinsic("Equals")
    fun same(left: T, right: T): Boolean

    @kotlin.clr.ClrIntrinsic("GetHashCode")
    fun hash(value: T): Int
}

private class KotlinHashComparer<T> : NativeHashComparer<T> {
    override fun same(left: T, right: T): Boolean = kotlinEquals(left, right)
    override fun hash(value: T): Int = kotlinHashCode(value)
}

@PublishedApi
internal fun <T> kotlinHashComparer(): NativeHashComparer<T> = KotlinHashComparer<T>()
