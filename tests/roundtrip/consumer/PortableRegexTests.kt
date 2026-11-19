package roundtriptests.portableregex

import NUnit.Framework.TestAttribute
import roundtrip.portableregex.*

class PortableRegexTests {
    @TestAttribute fun foreignRegexKeepsNativeMatchingAndDistinctReplacementSyntax() {
        val native = RegexInterop.NativeRegexes.Sparse()
        val regex = (native as Any) as Regex
        check(replaceFromLibrary(regex, "a", "<\$10:\$1:\${named}>") == "<a::>")
        var missing = false
        try { replaceFromLibrary(regex, "a", "\$2") }
        catch (_: IndexOutOfBoundsException) { missing = true }
        check(missing)
        check(native.Replace("a", "<\$&>") == "<a>")
        val backwards = (RegexInterop.NativeRegexes.Backwards("a.") as Any) as Regex
        check(backwards.replaceFirst("a1/a2", "<\$0>") == "a1/<a2>")
        check(replaceFromLibrary(backwards, "a1/a2", "<\$0>") == "<a1>/<a2>")
        val zeroWidth = RegexInterop.NativeRegexes.Backwards("a|")
        check(replaceFromLibrary((zeroWidth as Any) as Regex, "baa", "<\$0>") == zeroWidth.Replace("baa", "<\$&>"))
    }

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
        check(match.groups["tail"] == null)
        check(capturedGroup(match, "word")?.value == "a")
        check(capturedGroup(match, "tail") == null)
        check(capturedGroup(capturedMatch("ab"), "tail")?.value == "b")
        var missingHere = false
        try { match.groups["missing"] } catch (e: IllegalArgumentException) { missingHere = true }
        check(missingHere)
        var missingInProducer = false
        try { capturedGroup(match, "missing") } catch (e: IllegalArgumentException) { missingInProducer = true }
        check(missingInProducer)
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
