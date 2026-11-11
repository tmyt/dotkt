package kotc.bir

// Kotlin vocabulary only. Physical CLR decisions belong to bir2cir.
sealed interface BirValue {
    fun writeJson(output: StringBuilder)
}

sealed interface BirNode : BirValue {
    fun toJson(): String = buildString { writeJson(this) }
}

sealed interface BirStatement : BirNode
sealed interface BirExpression : BirStatement
sealed interface BirBranch : BirValue
