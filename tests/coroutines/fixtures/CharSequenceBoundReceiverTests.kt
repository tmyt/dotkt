package charseqboundreceiver

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private fun <A : CharSequence> readAll(value: A): String {
    val copied = value
    val readChar = { copied[1] }
    return "${value.length}:${readChar()}:${value.subSequence(1, 3)}"
}

private fun <A : CharSequence, B : A> transitive(value: B): Int = value.length
private fun <A : CharSequence?> nullableLength(value: A): Int = value?.length ?: -1
private fun <A : CharSequence?> presentChar(value: A & Any): Char = value[0]
private fun <A : CharSequence?> nullableChar(value: A): Char? = value?.get(1)

private fun preserve(block: suspend () -> String): suspend () -> String = block
private inline fun forward(crossinline block: suspend () -> String): suspend () -> String = preserve { block() }
private fun <A : CharSequence> dense(value: A): suspend () -> String = forward {
    "${value.length}:${value[1]}:${value.subSequence(1, 3)}"
}
private inline fun <A : CharSequence> inlineClosure(value: A): () -> String = {
    "${value[1]}:${value.subSequence(1, 3)}"
}

private class BoundOwner<Unused, A : CharSequence>(val unused: Unused, val label: A) {
    fun <B : CharSequence> read(other: B): Pair<String, String> = Pair(readAll(label), readAll(other))
    fun <B : CharSequence> deferred(other: B): suspend () -> Pair<Int, Char> = {
        val nested: suspend () -> Pair<Int, Char> = { Pair(label.length, other[1]) }
        nested()
    }
}

class CharSequenceBoundReceiverTests {
    @TestAttribute
    fun genericBoundsSelectConcreteStringReceivers() {
        check(nullableLength<String?>(null) == -1)
        check(nullableLength("ab") == 2)
        check(presentChar("ab") == 'a')
        check(nullableChar<String?>(null) == null)
        check(nullableChar("ab") == 'b')
        check(inlineClosure("abcd")() == "b:bc")
        var denseResult: String? = null
        dense("abcd").startCoroutine(object : Continuation<String> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<String>) { denseResult = result.getOrThrow() }
        })
        check(denseResult == "4:b:bc")
        check(readAll("abcd") == "4:b:bc")
        check(transitive<String, String>("abcde") == 5)
        val owner = BoundOwner(123, "abcd")
        check(owner.read("xyz") == Pair("4:b:bc", "3:y:yz"))
        var value: Pair<Int, Char>? = null
        owner.deferred("xyz").startCoroutine(object : Continuation<Pair<Int, Char>> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Pair<Int, Char>>) { value = result.getOrThrow() }
        })
        check(value == Pair(4, 'y'))
    }
}
