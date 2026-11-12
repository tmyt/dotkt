package kotc.bir

// Kotlin vocabulary only. Physical CLR decisions belong to bir2cir.
sealed interface BirValue {
    fun writeJson(output: StringBuilder)
}

sealed class BirNode : BirValue {
    fun toJson(): String = buildString { writeJson(this) }
}

sealed class BirStatement : BirNode()
sealed class BirExpression : BirNode()
sealed interface BirBranch : BirValue
sealed interface BirTypeDeclaration : BirValue
