package roundtrip.suspendinlineaction

class ActionTrace {
    var text: String = ""

    fun enter(owner: Any?) {
        text += if (owner == null) "E" else "O"
    }

    fun exit(owner: Any?) {
        text += if (owner == null) "F" else "X"
    }
}

suspend inline fun <T> runWithAction(
    trace: ActionTrace,
    owner: Any? = null,
    action: () -> T,
): T {
    trace.enter(owner)
    try {
        return action()
    } finally {
        trace.exit(owner)
    }
}
