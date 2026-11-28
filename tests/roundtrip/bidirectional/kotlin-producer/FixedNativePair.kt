import RoundtripDependentNative.ILeft
import RoundtripDependentNative.Base
import RoundtripDependentNative.JointValue
import RoundtripDependentNative.NativePairReader
import roundtrip.selectedbounds.Concrete
import roundtrip.selectedbounds.PairReader
import roundtrip.selectedbounds.QueueReader
import roundtrip.selectedbounds.SlotWriter
import kotlin.clr.ClrRef
import kotlin.clr.byref

open class GenericPairReader<K>(val saved: K) : NativePairReader(), PairReader<ILeft>, SlotWriter, QueueReader where K : Base, K : ILeft {
    override fun <T : Base> Describe(items: System.Collections.Generic.Queue<T>): String = "Kotlin"
    override fun <A> Pick(item: A): A where A : Base, A : ILeft = item
    override fun <A : ILeft, B : ILeft> Read(first: A, second: B): String = "Kotlin"
    override fun <A : ILeft> Replace(slot: ClrRef<A>, next: A): A {
        val old = slot.value; slot.value = next; return old
    }
}
open class KotlinPairReader : GenericPairReader<Concrete>(Concrete())

open class SelfKey : Base(), RoundtripDependentNative.IKey<SelfKey>
open class KotlinSelfReader : NativePairReader(), roundtrip.selectedbounds.SelfReader {
    override fun <T> PickSelf(item: T): T where T : Base, T : RoundtripDependentNative.IKey<T> = item
}
fun readSelf(reader: roundtrip.selectedbounds.SelfReader, key: SelfKey): SelfKey = reader.PickSelf<SelfKey>(key)

fun readQueue(reader: QueueReader): Char = reader.Describe(System.Collections.Generic.Queue<Concrete>())[0]

fun readPair(reader: PairReader<ILeft>, first: Concrete, second: JointValue): Any = reader.Read(first, second)

private class Item<T> : Base(), ILeft
fun pickPair(writer: SlotWriter, known: Concrete): Boolean {
    val hidden: Item<*> = Item<Int>()
    val projected: SlotWriter = GenericPairReader(hidden)
    return writer.Pick<Concrete>(known) === known && writer.Pick(hidden) === hidden && projected.Pick(hidden) === hidden
}

fun replacePair(writer: SlotWriter, first: Concrete, next: Concrete): Concrete {
    var slot = first
    val result = writer.Replace(byref(slot), next)
    check(slot === next)
    return result
}
