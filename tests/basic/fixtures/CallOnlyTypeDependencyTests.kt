package callonlytypedependency

import NUnit.Framework.TestAttribute

private fun <T> token(): Int = 29
private fun <T> liftedLambda(): () -> Int = { token<T>() }
private fun <T> liftedLocal(): Int {
    fun read(): Int = token<T>()
    return read()
}
private fun interface Probe { fun read(): Int }
private fun <T> liftedSam(): Probe = Probe { token<T>() }

class CallOnlyTypeDependencyTests {
    @TestAttribute
    fun callTypeArgumentsKeepTheirLexicalFrame() {
        check(liftedLambda<String>()() == 29)
        check(liftedLambda<Int>()() == 29)
        check(liftedLocal<String>() == 29)
        check(liftedLocal<Int>() == 29)
        check(liftedSam<String>().read() == 29)
        check(liftedSam<Int>().read() == 29)
    }
}
