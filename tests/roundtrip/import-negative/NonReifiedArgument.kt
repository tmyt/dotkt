import roundtrip.reifiednullability.matches

private fun <U> rejected(value: Any?): Boolean = matches<U>(value)
