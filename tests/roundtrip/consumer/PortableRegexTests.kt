package roundtriptests.portableregex

import NUnit.Framework.TestAttribute
import roundtrip.portableregex.*

class PortableRegexTests {
    @TestAttribute fun backwardsEngineMatchesAreExposedInInputOrder() {
        val regex = (RegexInterop.NativeRegexes.Backwards("a") as Any) as Regex
        val first = regex.find("aba")!!
        check(first.range.first == 0 && first.next()!!.range.first == 2 && first.next()!!.next() == null)
        check(regex.findAll("aba", 1).map { it.range.first }.toList() == listOf(2))
        check(regex.split("aba") == listOf("", "b", ""))
        check(regex.splitToSequence("aba", 2).toList() == listOf("", "ba"))
        val visited = mutableListOf<Int>()
        check(regex.replace("aba") { visited.add(it.range.first); "[" + it.range.first + "]" } == "[0]b[2]")
        check(visited == listOf(0, 2))
        val overlap = (RegexInterop.NativeRegexes.Backwards("aa") as Any) as Regex
        check(overlap.find("aaa")!!.range == 1..2 && overlap.find("aaa", 2) == null)
        val empty = (RegexInterop.NativeRegexes.Backwards("(?=a)|$") as Any) as Regex
        check(empty.findAll("aba").map { it.range.first }.toList() == listOf(0, 2, 3))
        val tied = (RegexInterop.NativeRegexes.Backwards("(?<ch>[ab])+|\\A") as Any) as Regex
        val atZero = tied.find("ab")!!
        check(atZero.value == "" && atZero.next()!!.groups["ch"]?.value == "a")
        check(tied.replace("ab") { "[" + it.value + "]" } == "[][ab]")
        check(empty.replace("aba") { "|" } == "|ab|a|")
    }

    @TestAttribute fun foreignRegexKeepsNativeMatchingAndDistinctReplacementSyntax() {
        val native = RegexInterop.NativeRegexes.Sparse()
        val regex = (native as Any) as Regex
        val first = regex.find("ab a")!!
        check(first.groups.size == 3 && first.groups[10]?.value == "a")
        check(first.groups.map { it?.value } == listOf("ab", "b", "a"))
        check(first.groupValues == listOf("ab", "b", "a"))
        val next = first.next()!!
        check(next.groups.map { it?.value } == listOf("a", null, "a"))
        check(next.destructured.toList() == listOf("", "a"))
        val alternate = (RegexInterop.NativeRegexes.WithAlternatePattern() as Any) as Regex
        val entire = alternate.matchEntire("a")!!
        check(entire.groups.size == 3 && entire.groups[10]?.value == "a")
        check(entire.groupValues == listOf("a", "", "a"))
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
