using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static ConstrainedReceiverPolicy;

static partial class ConstrainedTypeParameterReceiverBinding
{
    // Phase 1 only: do not author constrained dispatch or align arguments. The
    // inherited-owner walk still needs the receiver's constructed direct bound.
    internal static void CloseOpenOwners(IEnumerable<BirFileDeclaration> roots)
    {
        foreach (var file in roots)
        {
            foreach (var method in file.Methods.OrEmpty()) CloseMethod(method, Array.Empty<ConstraintSlot>());
            foreach (var type in file.Types.OrEmpty())
            {
                var facts = NullableRepresentationViews.TypeFacts((BirGraphValue)type);
                var frame = ResolvedConstraintFrame(TypeParameterFrame.Declarations(type).ToArray(),
                    DispatchBounds((BirGraphValue)type), facts?.Bounds, facts?.NullableFrame);
                foreach (var constructor in type.Ctors.OrEmpty())
                    CloseBody(constructor.Params.OrEmpty(), constructor.Body.OrEmpty(), frame, Array.Empty<ConstraintSlot>());
                foreach (var method in type.Methods.OrEmpty()) CloseMethod(method, frame);
            }
        }
        // The current property vocabulary contains accessor names/signatures,
        // not executable getter/setter records. Actual accessors are methods.
    }

    static void CloseMethod(BirFunctionDeclaration method, ConstraintSlot[] owner)
    {
        var frame = ResolvedConstraintFrame(method.TypeParams.OrEmpty(), method._ownerConstraintDispatchBounds.OrDefault(),
            method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds, null);
        CloseBody(method.Params.OrEmpty(), method.Body.OrEmpty(), owner, frame);
    }

    static void CloseBody(BirParameter[] parameters, IBirStatement[] body, ConstraintSlot[] owner, ConstraintSlot[] method)
        => CloseBody(parameters.Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())), body, owner, method, null);

    static void CloseBody(IEnumerable<(string Name, TypeNode Type)> parameters, IBirStatement[] body,
        ConstraintSlot[] owner, ConstraintSlot[] method, ReferenceMetadataIndex refs)
    {
        var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters) RecordLocal(locals, ambiguous, parameter.Name, parameter.Type);
        void Collect(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirVariable variable) RecordLocal(locals, ambiguous, variable.Name.OrDefault(), variable.Type.OrDefault());
            node.VisitChildren(Collect);
        }
        foreach (var statement in body) Collect((BirGraphValue)statement);

        TypeNode.Tv Receiver(IBirExpression expression)
        {
            if (expression is BirGraphValue value && value.ErasedValueVariableFact.OrDefault() is TypeNode.Tv retained) return retained;
            var type = expression is BirLocal { Name.HasValue: true } local
                && locals.TryGetValue(local.Name.Value, out var declared)
                    ? declared : BirGraphStaticType.Surface(expression, locals);
            return Peel(type) as TypeNode.Tv;
        }
        void Bind(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirNewSuspendLambda suspend)
            {
                foreach (var value in suspend.CapValues.OrEmpty())
                    if (value is BirGraphValue capture) Bind(capture);
                return;
            }
            var (selectedOwner, receiver) = node switch {
                BirCallInstance call => (call.OwnerType.OrDefault(), call.Recv.OrDefault()),
                BirNativeInstance call => (call.Type.OrDefault(), call.Recv.OrDefault()),
                BirGenericInstance call => (call.Type.OrDefault(), call.Recv.OrDefault()),
                BirPropertyGet call => (call.Type.OrDefault(), call.Recv.OrDefault()),
                BirPropertySet call => (call.Type.OrDefault(), call.Recv.OrDefault()),
                _ => ((TypeNode)null, (IBirExpression)null),
            };
            if (selectedOwner is TypeNode.Fqn named && receiver != null && Receiver(receiver) is { } variable)
            {
                var erased = false;
                var bound = refs == null ? ConstraintAt(variable, named.Name, owner, method, out erased)
                    : ConstraintAtPhysical(variable, named.Name, owner, method, refs, out erased);
                if (bound != null)
                {
                    var closed = (named.Args == null || erased) && bound.Args != null ? bound : named;
                    // Preserve the actual node and every unrelated call fact. Only
                    // the direct owner and selected-route provenance belong here.
                    switch (node)
                    {
                        case BirCallInstance call: call.OwnerType = closed; call._erasedConstraintDispatch = erased; break;
                        case BirNativeInstance call: call.Type = closed; call._erasedConstraintDispatch = erased; break;
                        case BirGenericInstance call: call.Type = closed; call._erasedConstraintDispatch = erased; break;
                        case BirPropertyGet call: call.Type = closed; call._erasedConstraintDispatch = erased; break;
                        case BirPropertySet call: call.Type = closed; call._erasedConstraintDispatch = erased; break;
                    }
                }
            }
            node.VisitChildren(Bind);
        }
        foreach (var statement in body) Bind((BirGraphValue)statement);
    }
}
