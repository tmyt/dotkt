package inheritedcarrier

interface RunnableContract { fun run(): Int }
interface NameContract<T> { fun name(): T }
abstract class Root : RunnableContract {
    override fun run(): Int = 43
}
abstract class Middle : Root(), NameContract<String> {
    override fun name(): String = "referenced"
}
