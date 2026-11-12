package kotc.backend

import kotc.bir.*

/** Source join-null facts belong to the node, not to JSON property order.
 * Keep the root-only rule aligned with [emittedYieldsNull] at the remaining string frontier. */
internal fun BirExpression.yieldsNullAtJoin(): Boolean = when (this) {
    is BirConstant -> value == BirLiteral.NullLiteral
    is BirConditional -> joinNullBranch == true
    is BirValueBlock -> joinNullBranch == true
    is BirObjectEquals, is BirLocal, is BirThis, is BirNew, is BirThrowExpression,
    is BirUnaryOperation, is BirNullableHasValue, is BirNullableValue,
    is BirEnumValue, is BirEnumValues, is BirCompanionValue, is BirClassReference,
    is BirNullableNull, is BirDefaultParameter, is BirDefaultReceiver,
    is BirBindingReference, is BirLocalFunctionReference,
    is BirBinaryOperation, is BirCast, is BirIsInstance, is BirInstanceOrNull,
    is BirSafeValueCast, is BirConcat, is BirRuntimeClass, is BirNullableWrap,
    is BirArrayGet, is BirArraySet, is BirArrayLength, is BirNewArray,
    is BirNewArraySized, is BirNewArrayInit, is BirSpreadConcat, is BirDefaultValue,
    is BirEnumName, is BirEnumOrdinal, is BirEnumParse, is BirObjectMethod,
    is BirCallEvaluation, BirDefaultArgument,
    is BirCallStatic, is BirCallInstance, is BirCallLocal,
    is BirFieldGet, is BirStaticFieldGet, is BirFieldSetExpression, is BirStaticFieldSet,
    is BirLateinitGet, is BirLateinitIsInitialized, is BirCallInline, is BirNewBoundDelegate,
    is BirByRefOf, is BirByRefLoad, is BirByRefStore, is BirReturnExpression,
    is BirUnsupportedExpression -> false
}

/** The result is the only pass-through value; statements contribute only effects. */
internal fun sourceValueBlock(
    statements: List<BirStatement>,
    result: BirExpression,
    type: TypeNode? = null,
    yieldsNull: Boolean = result.yieldsNullAtJoin(),
): BirValueBlock = BirValueBlock(statements, result, type = type,
    joinNullBranch = if (yieldsNull) true else null)
