import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

class ExistentialOwnerHolder(val owner: ExistentialSlotOwner<String>)
class ExistentialItemHolder<T>(val item: ExistentialSlotOwner<T>.Item)

interface ExistentialOuterReader { fun read(): String }
class ExistentialAnonymousOwner<T>(private val value: T) {
    fun reader(): ExistentialOuterReader = object : ExistentialOuterReader {
        override fun read(): String = value.toString()
    }
}

fun interface ExistentialImportedNestedReceiver<T> { fun accept(value: T): Int }
fun <T> existentialImportedNestedReceiver(expected: T): ExistentialImportedNestedReceiver<ExistentialSlotOwner<T>> =
    ExistentialImportedNestedReceiver { check(it.value == expected); 23 }

open class ExistentialLocalBase(val label: String)
class ExistentialLocalDerived<T>(val value: T) : ExistentialLocalBase("base")
fun existentialAcceptLocalBase(value: ExistentialLocalBase): String = value.label
fun <T> existentialCheckLocalBase(value: T): Boolean {
    val derived = ExistentialLocalDerived(value)
    val base: ExistentialLocalBase = derived
    return existentialAcceptLocalBase(derived) == "base" && base === derived
}

class ExistentialFieldStorage<T : Any> {
    lateinit var value: T
    @kotlin.clr.ClrField var counter: Int = 0
    fun read(): T = value
    fun write(next: T) { value = next }
}

class ExistentialGenericInitializer<T>(size: Int) {
    private val values = Array(size) { ExistentialSlotOwner<T?>(null) }
    fun count(): Int = values.size
    fun read(index: Int): T? = values[index].value
}

class ExistentialInnerArrayOwner<T> {
    inner class Node
    inner class Disposal(private val nodes: Array<Node>) {
        fun count(): Int = nodes.size
    }
    fun countFromClosure(): Int {
        val action = {
            val nodes = Array(2) { Node() }
            Disposal(nodes).count()
        }
        return action()
    }
    suspend fun countSuspended(): Int = suspendCoroutine { continuation ->
        val nodes = Array(2) { Node() }
        continuation.resume(Disposal(nodes).count())
    }
}

fun <T> existentialDisposeNodes(
    owner: ExistentialInnerArrayOwner<T>,
    nodes: Array<ExistentialInnerArrayOwner<T>.Node>,
): Int = owner.Disposal(nodes).count()

fun <T> existentialOwnerList(value: T): List<ExistentialSlotOwner<T>> {
    val values = ArrayList<ExistentialSlotOwner<T>>()
    values.add(ExistentialSlotOwner(value))
    return values
}

class ExistentialSlotOwner<T>(val value: T) {
    inner class Item(val count: Int)
    private var stored: Item? = null
    fun store(raw: Any) { stored = raw as ExistentialSlotOwner<T>.Item }
    fun storedItem(): Item? = stored
    private fun consume(item: Item): Int = item.count
    private val secret: Int = 17
    private inline fun withSecret(action: () -> Int): Int = secret + action()
    fun inlineRead(item: Item): Int = withSecret { consume(item) }
    fun readPrivate(other: ExistentialSlotOwner<T>, raw: Any): Int =
        other.consume(raw as ExistentialSlotOwner<T>.Item)
    fun read(item: Item): Int = consume(item)
    fun same(item: Item, raw: Any): Boolean = item === raw
    fun nullable(item: Item?): Int = item?.count ?: 0
    fun fromAny(raw: Any): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        return consume(item)
    }
    fun unused(raw: Any): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        return 7
    }
    suspend fun suspended(raw: Any): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        val count = consume(item)
        suspendCoroutine<Unit> { it.resume(Unit) }
        return count
    }
    suspend fun deferred(raw: Any, pause: suspend () -> Unit): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        pause()
        return consume(item)
    }
}

class ExistentialHierarchyOwner<T> {
    open inner class Base(val count: Int)
    inner class Derived : Base(23)
    private fun consume(value: Base): Int = value.count
    fun fromAny(raw: Any): Int {
        val value = raw as ExistentialHierarchyOwner<T>.Derived
        return consume(value)
    }
}
