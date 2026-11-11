package kotc.bir

// Kotlin vocabulary only. Physical CLR decisions belong to bir2cir.
sealed interface BirNode {
    fun writeJson(output: StringBuilder)
    fun toJson(): String = buildString { writeJson(this) }
}

sealed interface BirExpression : BirNode
sealed interface BirStatement : BirNode
