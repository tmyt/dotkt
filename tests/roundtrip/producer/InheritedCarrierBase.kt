package inheritedcarrier

interface RunnableContract { fun run(): Int }
interface NameContract<T> { fun name(): T }
abstract class Root : RunnableContract {
    @get:kotlin.clr.ClrName("\$star\$read")
    var tag: Int = 42
    override fun run(): Int = 43
}
abstract class Middle : Root(), NameContract<String> {
    override fun name(): String = "referenced"
}
class TaggedLeaf<T>(val item: T) : Middle()

open class GenericBase<T>(private val item: T) : NameContract<T> {
    override fun name(): T = item
}
open class GenericMiddle : GenericBase<String>("indirect")
fun genericBaseName(value: GenericBase<*>): Any? = value.name()
interface UnitContract<T> { fun stamp(): Int }
abstract class UnitMiddle : UnitContract<Unit> {
    override fun stamp(): Int = 45
}
