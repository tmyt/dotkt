package kotc.bir

// Kotlin vocabulary only. Physical CLR decisions belong to bir2cir.
sealed interface BirValue {
    fun writeJson(output: StringBuilder)
}

sealed class BirNode : BirValue {
    fun toJson(): String = buildString { writeJson(this) }

    // Declaration output still has textual frontiers during the source migration.
    // Interpolation writes the node; it must never expose a data-class debug string.
    final override fun toString(): String = toJson()
}

sealed class BirStatement : BirNode()
sealed class BirExpression : BirNode()
sealed interface BirBranch : BirValue
