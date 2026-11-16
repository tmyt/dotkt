using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        static BirLocal Local(string name) => new() { Name = name };
        static BirReturn Ret(IBirExpression value) => new() { Value = OptionalField<IBirExpression>.Of(value) };
        static BirConstant IntConst(int value) => new() { Type = IntTn, Value = new BirLiteral.IntegerLiteral(value) };
        static BirConstant BoolConst(bool value) => new() { Type = BoolTn, Value = new BirLiteral.BooleanLiteral(value) };
        static BirConstant NullConst(TypeNode type) => new() { Type = type, Value = new BirLiteral.NullLiteral() };
        static BirDefaultValue DefaultOf(TypeNode type) => new() { Type = type };
        static BirLabel Label(int id) => new() { Id = id };
        static BirGoto Goto(int id) => new() { Id = id };
        static BirBranchIf BrIf(IBirExpression condition, bool on, int id) => new() {
            Cond = OptionalField<IBirExpression>.Of(condition), On = on, Id = id };
        static BirBinaryOperation BinEq(IBirExpression left, IBirExpression right) => new() {
            Op = "==", Lhs = OptionalField<IBirExpression>.Of(left), Rhs = OptionalField<IBirExpression>.Of(right) };
        static BirStaticFieldGet UnitValue() => new() { OwnerType = UnitTn, Name = "INSTANCE", Sty = UnitTn };
        static TypeNode ContAny() => BirGraphValue.CopyType(ContAnyTn);
        static TypeNode NullableContAny() => BirGraphValue.CopyType(NullableContAnyTn);
        static TypeNode ContUnit() => BirGraphValue.CopyType(ContUnitTn);
        TypeNode ContStar() => BirGraphValue.CopyType(_module.ContinuationStar);
        static BirCallStatic Suspended() => new() {
            Owner = new TypeNode.Fqn(IntrinsicsKtFqn), Method = "COROUTINE_SUSPENDED", Prop = "get",
            Args = Array.Empty<IBirExpression>(), Ret = AnyTn };
        static BirCallStatic ThrowOnFailure() => new() {
            Owner = new TypeNode.Fqn(ThrowOnFailureOwner), Method = "throwOnFailure",
            Sig = new TypeNode[] { new TypeNode.Nullable(AnyTn) },
            Args = new IBirExpression[] { Local(ResumeResultParameter) }, Ret = VoidTn };

        BirSetField SetField(string name, IBirExpression value)
        {
            var assignment = new BirSetField { OwnerType = _smTypeInst, Recv = new BirThis(), Name = name,
                Value = OptionalField<IBirExpression>.Of(value) };
            if (IsExactOuterStorage(name)) assignment.Outer = true;
            return assignment;
        }

        BirFieldGet FieldOf(string name, TypeNode type)
        {
            var field = new BirFieldGet { OwnerType = _smTypeInst, Recv = new BirThis(), Name = name, Ret = type };
            if (IsExactOuterStorage(name)) field.Outer = true;
            return field;
        }

        BirCallInstance CoroutineContextValue(bool smPath) => new() {
            OwnerType = new TypeNode.Fqn(smPath ? ContinuationImplFqn : ContinuationFqn),
            Method = "context", Prop = "get", Virtual = true,
            Recv = OptionalField<IBirExpression>.Of(smPath ? new BirThis() : Local(CompletionParameter)),
            ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(),
            Ret = new TypeNode.Fqn(CoroutineContextFqn),
        };

        // This is the no-SM owning path: it changes the selected read, not the
        // identity of all its surrounding source statements/callables.
        BirGraphValue ReplaceCoroutineContextDirect(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            if (node is BirCallStatic call && IsCoroutineContextRead("callStatic",
                call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Method.OrDefault(),
                call.Prop.OrDefault(), call.Args.HasValue ? call.Args.Value.Length : null))
                return CoroutineContextValue(false);
            node.VisitChildSlots(slot => slot.Replace(ReplaceCoroutineContextDirect(slot.Value)));
            return node;
        }

        static BirGraphValue MaterializeUnitReturns(BirGraphValue node)
        {
            if (node is IBirExecutablePayload || IsSourceOtherFrame(node)) return node;
            node.VisitChildSlots(slot => slot.Replace(MaterializeUnitReturns(slot.Value)));
            if (node is BirReturn statement && !statement.Value.HasValue) statement.Value = UnitValue();
            else if (node is BirReturnExpression expression && !expression.Value.HasValue) expression.Value = UnitValue();
            return node;
        }

        IBirExpression MissingValuePlaceholder(TypeNode expectedType)
        {
            var type = expectedType ?? throw new NotSupportedException(
                $"bir2cir: suspend-lowering: a result-less suspension-bearing valueBlock in `{DiagOwner}` has no enclosing expected type.");
            return IsUnitTn(type) ? NullConst(type) : DefaultOf(type);
        }
    }
}
