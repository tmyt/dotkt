import NUnit.Framework.NonParallelizableAttribute
import NUnit.Framework.TestAttribute
import kotlin.printStackTrace as printDetailedStackTrace

private class RenderEqualException(private val label: String) : RuntimeException(label) {
    override fun equals(other: Any?): Boolean = other is RenderEqualException
    override fun hashCode(): Int = error("Rendering must not call user hashCode")
    override fun toString(): String = label
}

private class RenderCallbackException(private val onRender: () -> Unit) : RuntimeException("render callback") {
    override fun toString(): String { onRender(); return "render-callback-description" }
}

// Console.Error is process-wide; do not race other fixtures while capturing it.
@NonParallelizableAttribute
class SuppressedExceptionRenderingTests {
    @TestAttribute
    fun keepsNativeDetailsAndRendersSuppressionInOrder() {
        val cause = RuntimeException("native-cause")
        val plain = try { throw IllegalStateException("native-primary", cause) }
            catch (failure: Throwable) { failure }
        val nativeException = plain as System.Exception
        val native = nativeException.ToString()
        check(plain.stackTraceToString() == native)
        plain.addSuppressed(IllegalArgumentException("suppressed-first-marker"))
        plain.addSuppressed(RuntimeException("suppressed-second-marker"))
        val text = plain.stackTraceToString()
        check(text.startsWith(native))
        check(text.contains("native-primary") && text.contains("native-cause")) { "Native: $native\nRendered: $text" }
        val first = text.indexOf("suppressed-first-marker")
        val second = text.indexOf("suppressed-second-marker")
        check(first >= 0 && second > first)
        check(nativeException.ToString() == native)
    }

    @TestAttribute
    fun includesCauseSideAndNestedSuppression() {
        val deepestCause = RuntimeException("deepest-cause")
        deepestCause.addSuppressed(RuntimeException("deep-cause-suppressed-marker"))
        val cause = RuntimeException("cause", deepestCause)
        cause.addSuppressed(RuntimeException("cause-suppressed-marker"))
        val primary = RuntimeException("root", cause)
        val suppressedCause = RuntimeException("suppressed-cause")
        suppressedCause.addSuppressed(RuntimeException("suppressed-cause-child-marker"))
        val suppressed = RuntimeException("suppressed", suppressedCause)
        suppressed.addSuppressed(RuntimeException("nested-suppressed-marker"))
        primary.addSuppressed(suppressed)
        val text = primary.stackTraceToString()
        for (marker in listOf("deep-cause-suppressed-marker", "cause-suppressed-marker",
                "suppressed-cause-child-marker", "nested-suppressed-marker")) check(text.contains(marker))
    }

    @TestAttribute
    fun cyclesAndSharedNodesUseReferenceIdentity() {
        val primary = RuntimeException("cycle-root")
        val left = RenderEqualException("equal-left-marker")
        val right = RenderEqualException("equal-right-marker")
        check(left == right && left !== right)
        primary.addSuppressed(left)
        primary.addSuppressed(right)
        primary.addSuppressed(left)
        left.addSuppressed(primary)
        right.addSuppressed(left)
        val text = primary.stackTraceToString()
        check(text.contains("cycle-root"))
        check(text.contains("equal-left-marker") && text.contains("equal-right-marker"))
        check(text.length < 10_000)
    }

    @TestAttribute
    fun bothPrintPathsUseSnapshotsAndRenderOutsideSuppressionLocks() {
        val primary = RuntimeException("print-primary")
        val late = RuntimeException("added-during-render-marker")
        var worker: System.Threading.Thread? = null
        var first = true
        primary.addSuppressed(RenderCallbackException {
            if (first) {
                first = false
                val thread = System.Threading.Thread(System.Threading.ThreadStart { primary.addSuppressed(late) })
                worker = thread
                thread.IsBackground = true
                thread.Start()
                check(thread.Join(10_000))
            }
        })
        val previous = System.Console.Error
        val writer = System.IO.StringWriter()
        try {
            val snapshotText = primary.stackTraceToString()
            check(snapshotText.contains("render-callback-description"))
            check(!snapshotText.contains("added-during-render-marker"))
            check(primary.suppressedExceptions.last() === late)
            System.Console.SetError(writer)
            primary.printStackTrace()
            val memberText = writer.ToString()
            check(memberText.contains("render-callback-description") && memberText.contains("added-during-render-marker"))
            primary.printDetailedStackTrace()
            check(writer.ToString() == memberText + memberText)
        } finally {
            System.Console.SetError(previous)
            writer.Dispose()
            worker?.Join(10_000)
        }
    }
}
