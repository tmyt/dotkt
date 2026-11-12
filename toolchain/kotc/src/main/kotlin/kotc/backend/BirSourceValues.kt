package kotc.backend

import kotc.bir.*

/** Only the enclosing receiver chain is rebased. Nested declaration bodies own their own `this`. */
internal fun BirExpression.withDefaultDispatchRoot(): BirExpression = when (this) {
    is BirThis -> BirDefaultReceiver("dispatch")
    is BirFieldGet -> copy(recv = recv.withDefaultDispatchRoot())
    else -> this
}

/** Source join-null facts belong to the VALUE at the root, not a descendant or JSON property order.
 * Only a bare null constant and a conditional/value-block's own join fact are transparent.
 * Casts, nullable construction/unwrapping and control transfers do not pass a descendant's null through. */
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
    is BirUnsupportedExpression, is BirInlineLambda, is BirStackAllocation,
    is BirStackGet, is BirStackSet, is BirStackAsSpan, is BirEventGet, is BirEventRaise,
    is BirNewClosure, is BirNewDelegate, is BirNewSuspendLambda, is BirSamConversion, is BirNewSam,
    is BirDefaultCarrier -> false
}

/** The result is the only pass-through value; statements contribute only effects. */
internal fun sourceValueBlock(
    stmts: List<BirStatement>,
    result: BirExpression,
    type: TypeNode? = null,
    yieldsNull: Boolean = result.yieldsNullAtJoin(),
): BirValueBlock = BirValueBlock(stmts, result, type = type,
    joinNullBranch = if (yieldsNull) true else null)
