package roundtriptests.portableregex

import NUnit.Framework.TestAttribute
import roundtrip.portableregex.*

class PortableRegexTests {
    @TestAttribute fun librarySequencesCanOutliveTheProducerCallAndBeReiterated() {
        val sequence = splitLazily(Regex(","), "a,b,c", 2)
        check(sequence.toList() == listOf("a", "b,c"))
        check(sequence.toList() == listOf("a", "b,c"))
        val matches = matchesFrom("x a ab", 2)
        check(matches.map { it.value }.toList() == listOf("a", "ab"))
        check(matches.map { it.value }.toList() == listOf("a", "ab"))
    }

    @TestAttribute fun importedMatchesKeepCommonDefaultsAndCollectionSlots() {
        val match: MatchResult = capturedMatch("a")
        check(match.groupValues == listOf("a", "a", ""))
        check(match.destructured.toList() == listOf("a", ""))
        check(match.groups.containsAll(listOf(match.groups[0], null)))
        check(match.groups["word"]?.value == "a")
        val iterator = match.groups.iterator()
        repeat(match.groups.size) { iterator.next() }
        check(!iterator.hasNext())
        var rejected = false
        try { iterator.next() } catch (e: NoSuchElementException) { rejected = true }
        check(rejected)
        var calls = 0
        val text = observeCapturedGroups("ab") {
            calls++
            it.destructured.toList().joinToString(":")
        }
        check(calls == 1 && text == "a:b")
    }
}
