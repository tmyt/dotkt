package kotc.backend

import kotc.bir.*

/** Source join-null facts belong to the node, not to JSON property order. */
internal fun BirExpression.yieldsNullAtJoin(): Boolean = when (this) {
    is BirConstant -> value == BirLiteral.NullLiteral
    is BirConditional -> joinNullBranch == true
    is BirValueBlock -> joinNullBranch == true
    is BirObjectEquals, is BirLocal, is BirThis, is BirNew, is BirThrowExpression,
    is BirUnaryOperation, is BirNullableHasValue, is BirNullableValue -> false
}

/** The result is the only pass-through value; statements contribute only effects. */
internal fun sourceValueBlock(
    statements: List<BirStatement>,
    result: BirExpression,
    type: TypeNode? = null,
    yieldsNull: Boolean = result.yieldsNullAtJoin(),
): BirValueBlock = BirValueBlock(statements, result, type = type,
    joinNullBranch = if (yieldsNull) true else null)
