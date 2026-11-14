using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ErasedElementValues
{
    sealed class SourceSlot(BirGraphValue declaration, string name, TypeNode type)
    {
        internal readonly BirGraphValue Declaration = declaration;
        internal readonly string Name = name;
        internal readonly TypeNode Type = type;
    }

    // This is the early scalar/capture-storage mode, not the later array and
    // iterator element pass. Its local type/frame overlay is one file's current
    // declarations, matching the lifetime of the original early caller.
    internal static void ApplyScalarCalls(BirFileDeclaration file, ReferenceMetadataIndex references,
        ValueTypeOracle isValue, GenericRepresentationPolicy policy, Func<BirGraphValue, TypeNode.Tv> scalarResults)
    {
        var interfaces = new Dictionary<string, bool>(StringComparer.Ordinal);
        var frames = references?.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal) ?? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal);
        foreach (var type in file.Types.OrEmpty())
        {
            if (!type.Name.HasValue) continue;
            interfaces[type.Name.Value] = type.Kind.OrDefault() == "interface";
            if (NullableRepresentationViews.TypeFacts((BirGraphValue)type)?.NullableFrame is { } frame)
                frames[type.Name.Value] = frame;
        }
        bool IsInterface(TypeNode type) => type is TypeNode.Fqn named
            && (interfaces.TryGetValue(named.Name, out var local) ? local : references?.IsInterfaceType(named) == true);
        foreach (var method in file.Methods.OrEmpty()) ProjectSource(method, null, IsInterface, frames, isValue, policy, scalarResults);
        foreach (var type in file.Types.OrEmpty())
        {
            foreach (var method in type.Methods.OrEmpty()) ProjectSource(method, type, IsInterface, frames, isValue, policy, scalarResults);
            foreach (var constructor in type.Ctors.OrEmpty()) ProjectSource(constructor, type, IsInterface, frames, isValue, policy, scalarResults);
        }
    }

    static void ProjectSource(BirGraphValue declaration, IBirTypeDeclaration owner, Func<TypeNode, bool> isInterface,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy, Func<BirGraphValue, TypeNode.Tv> scalarResults)
    {
        var method = declaration as BirFunctionDeclaration;
        var constructor = declaration as BirConstructor;
        var parameters = method?.Params.OrEmpty() ?? constructor.Params.OrEmpty();
        var methodFrame = method?.NullableRepresentationFrame.OrDefault();
        var ownerFacts = owner == null ? null : NullableRepresentationViews.TypeFacts((BirGraphValue)owner);
        var methodBounds = new BoundDeclaration(methodFrame,
            method?.TypeParams.OrEmpty().Select(parameter => parameter.Constraints.OrEmpty()).ToArray() ?? [],
            method?.MethodTypeParameterBoundsPre.OrDefault()?.Bounds);
        var ownerBounds = new BoundDeclaration(ownerFacts?.NullableFrame,
            owner == null ? [] : owner.CapturedTypeParams.OrEmpty().Concat(owner.TypeParams.OrEmpty())
                .Select(parameter => parameter.Constraints.OrEmpty()).ToArray(), ownerFacts?.Bounds);
        IEnumerable<BirGraphValue> Executable() => constructor != null
            ? constructor.PreStmts.OrEmpty().Cast<BirGraphValue>()
                .Concat((constructor.ThisArgs.OrDefault() ?? constructor.BaseArgs.OrEmpty()).Cast<BirGraphValue>())
                .Concat(constructor.Body.OrEmpty().Cast<BirGraphValue>())
            : method.Body.OrEmpty().Cast<BirGraphValue>();

        var hasBody = method?.Body.HasValue ?? constructor.Body.HasValue;
        if (hasBody)
        {
            var original = constructor != null ? constructor.PreStmts.OrEmpty() : method.Body.Value;
            var prologue = original.ToList();
            TypedPhysicalValueCoercion.AllocateNativeLocals(Executable(), parameters, prologue, genericParametersOnly: true);
            if (prologue.Count != original.Length)
            {
                if (constructor != null) constructor.PreStmts = prologue.ToArray();
                else method.Body = prologue.ToArray();
            }
        }
        var executable = Executable().ToArray();
        if (executable.Length == 0) return;
        var reads = new Dictionary<BirGraphValue, TypeNode.Tv>(ReferenceEqualityComparer.Instance);
        var scalarReads = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        var captures = new List<BirGraphValue>();
        var uses = new Dictionary<BirGraphValue, SourceSlot>(ReferenceEqualityComparer.Instance);
        var addressed = new HashSet<SourceSlot>(ReferenceEqualityComparer.Instance);
        var slots = new List<SourceSlot>();
        var valueResults = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        var bindings = new Dictionary<string, SourceSlot>(StringComparer.Ordinal);
        var parameterOrigins = new Dictionary<SourceSlot, TypeNode.Tv>(ReferenceEqualityComparer.Instance);
        var mapping = new NullableRepresentationTypes(ownerFacts?.NullableFrame, methodFrame, frames, isValue, policy: policy);
        TypeNode.Tv SourceVariable(TypeNode source) => source is TypeNode.Tv variable ? Variable(mapping.Slot(variable)) : null;
        var erasedFields = (owner?.Fields.OrEmpty() ?? []).Where(field => IsObject(field.Type.OrDefault())
            && SourceVariable(field.KotlinType.OrDefault()) != null).ToDictionary(field => field.Name.OrDefault(),
                field => SourceVariable(field.KotlinType.OrDefault()), StringComparer.Ordinal);
        foreach (var parameter in parameters)
            if (parameter.Name.HasValue)
            {
                var slot = new SourceSlot(parameter, parameter.Name.Value, parameter.Type.OrDefault());
                bindings[slot.Name] = slot;
                if (slot.Type is not TypeNode.Tv && SourceVariable(parameter.KotlinType.OrDefault()) is { } variable)
                    parameterOrigins[slot] = variable;
            }

        void BindSequence(IEnumerable<IBirStatement> statements, Dictionary<string, SourceSlot> scope)
        {
            var block = new Dictionary<string, SourceSlot>(scope, StringComparer.Ordinal);
            foreach (var statement in statements) Bind((BirGraphValue)statement, block);
        }
        void Bind(BirGraphValue node, Dictionary<string, SourceSlot> scope, bool location = false)
        {
            if (node == null || node is IBirExecutablePayload || SourceNestedDeclaration(node)) return;
            if (node is BirNewClosure or BirNewSam or BirNewSuspendLambda) captures.Add(node);
            var usedName = node switch { BirLocal local => local.Name.OrDefault(), BirSetLocal set => set.Name.OrDefault(), _ => null };
            if (usedName != null && scope.TryGetValue(usedName, out var used))
            {
                uses[node] = used;
                if (location) addressed.Add(used);
            }
            if (!location && scalarResults?.Invoke(node) is { } scalarResult)
            {
                reads[node] = scalarResult;
                scalarReads.Add(node);
            }
            if (!location && node is BirFieldGet { Recv.HasValue: true } fieldRead && fieldRead.Recv.Value is BirThis
                && fieldRead.OwnerType.OrDefault() is TypeNode.Fqn named && named.Name == owner?.Name.OrDefault()
                && fieldRead.Name.HasValue && erasedFields.TryGetValue(fieldRead.Name.Value, out var fieldVariable))
                reads[node] = fieldVariable;
            if (!location && node is BirConditional or BirValueBlock) valueResults.Add(node);
            if (node is BirValueBlock valueBlock)
            {
                var inner = new Dictionary<string, SourceSlot>(scope, StringComparer.Ordinal);
                foreach (var statement in valueBlock.Stmts.OrEmpty()) Bind((BirGraphValue)statement, inner);
                Bind(valueBlock.Result.OrDefault() as BirGraphValue, inner, location);
                return;
            }
            if (node is BirForArray { Var.HasValue: true } loop)
            {
                Bind(loop.Array.OrDefault() as BirGraphValue, scope);
                var inner = new Dictionary<string, SourceSlot>(scope, StringComparer.Ordinal) {
                    [loop.Var.Value] = new SourceSlot(loop, loop.Var.Value, loop.Elem.OrDefault()),
                };
                BindSequence(loop.Body.OrEmpty(), inner);
                return;
            }
            var callParameters = SourceAddressParameters(node);
            var argument = 0;
            node.VisitSourceChildren((field, _, child) => {
                if (SourceSkip(field, node)) return;
                var address = field == "args" && callParameters != null && argument < callParameters.Length
                    && SignatureValueTypes.Of(callParameters[argument]) is TypeNode.ByRef;
                if (field == "args") argument++;
                Bind(child.Value, scope, address || node is BirByRefOf && field == "inner");
            }, region => {
                if (node is BirNewClosure or BirNewSam or BirNewSuspendLambda) return;
                BindSequence(region.Statements, scope);
                if (region.Result is { } result) Bind(result.Value, scope);
            });
            if (node is BirVariable { Name.HasValue: true } variable)
            {
                var slot = new SourceSlot(variable, variable.Name.Value, variable.Type.OrDefault());
                slots.Add(slot);
                scope[slot.Name] = slot;
            }
        }
        foreach (var statement in executable) Bind(statement, bindings);
        if (reads.Count == 0 && parameterOrigins.Count == 0) return;

        var origins = new Dictionary<SourceSlot, TypeNode.Tv>(parameterOrigins, ReferenceEqualityComparer.Instance);
        TypeNode.Tv Origin(BirGraphValue node)
        {
            if (node == null) return null;
            if (reads.TryGetValue(node, out var read)) return read;
            if (node is BirLocal && uses.TryGetValue(node, out var slot)) return origins.GetValueOrDefault(slot);
            if (node is BirCast { ReifiedTypeOperand.HasValue: true, ReifiedTypeOperand.Value: false, Type.HasValue: true } cast
                && cast.Type.Value is TypeNode.Tv target && Origin(cast.E.OrDefault() as BirGraphValue) == target) return target;
            if (node is BirValueBlock block) return Origin(block.Result.OrDefault() as BirGraphValue);
            if (node is BirConditional conditional) return Origin(conditional.Then.OrDefault() as BirGraphValue)
                ?? Origin(conditional.Else.OrDefault() as BirGraphValue);
            return null;
        }
        bool Record(SourceSlot slot, BirGraphValue value)
        {
            if (addressed.Contains(slot) || origins.ContainsKey(slot) || Origin(value) is not { } source) return false;
            if (slot.Type is TypeNode.Tv variable) origins[slot] = variable;
            else if (IsObject(slot.Type)) origins[slot] = source;
            else return false;
            return true;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var slot in slots) changed |= Record(slot, ((BirVariable)slot.Declaration).Init.OrDefault() as BirGraphValue);
            foreach (var (use, slot) in uses)
                if (use is BirSetLocal set) changed |= Record(slot, set.Value.OrDefault() as BirGraphValue);
        } while (changed);

        var casts = new Dictionary<BirCast, TypeNode>(ReferenceEqualityComparer.Instance);
        var constrained = new HashSet<BirConstrainedCall>(ReferenceEqualityComparer.Instance);
        var erasedResults = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        TypeNode.Tv LogicalVariable(BirGraphValue value)
        {
            TypeNode.Tv variable;
            if (value is BirLocal && uses.TryGetValue(value, out var slot))
                variable = slot.Type as TypeNode.Tv ?? parameterOrigins.GetValueOrDefault(slot);
            else variable = reads.GetValueOrDefault(value) ?? Variable(SourceValueType(value));
            if (variable == null) return null;
            var frame = variable.Scope == "method" ? methodFrame : ownerFacts?.NullableFrame;
            return frame == null ? variable : frame.Variable(new TypeNode.Tv(variable.Scope,
                frame.PhysicalSlot(variable.I).SourceIndex), NullableRepresentationFrame.Role.Ordinary);
        }
        void Plan(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload || SourceNestedDeclaration(node)) return;
            if (Origin(node) != null && LogicalVariable(node) is { } sourceVariable) node.ErasedValueVariableFact = sourceVariable;
            if (node is BirCast { ReifiedTypeOperand.HasValue: true, ReifiedTypeOperand.Value: false, Type.HasValue: true } cast
                && cast.Type.Value is TypeNode.Tv target && Origin(cast.E.OrDefault() as BirGraphValue) == target)
                casts[cast] = ErasedBound(target, methodBounds, ownerBounds, isInterface, frames, isValue, policy);
            if (node is BirConstrainedCall call && Origin(call.Recv.OrDefault() as BirGraphValue) != null) constrained.Add(call);
            if (valueResults.Contains(node) && Origin(node) != null)
            {
                var variableResult = false;
                node.VisitOwnTypeFields((field, type) => { if (field is "type" or "sty" && type is TypeNode.Tv) variableResult = true; });
                if (variableResult) erasedResults.Add(node);
            }
            node.VisitOwnChildSlots((field, _, child) => { if (!SourceSkip(field, node)) Plan(child.Value); });
        }
        foreach (var statement in executable) Plan(statement);
        foreach (var (read, _) in reads)
            switch (read)
            {
                case BirCallStatic call when scalarReads.Contains(read):
                    call.Ret = ObjectType; if (call.DynRet.HasValue) call.DynRet = ObjectType; call.Sty = ObjectType; break;
                case BirCallInstance call when scalarReads.Contains(read):
                    call.Ret = ObjectType; if (call.DynRet.HasValue) call.DynRet = ObjectType; call.Sty = ObjectType; break;
                case BirFieldGet field: field.Sty = ObjectType; break;
                default: throw new InvalidOperationException("Unknown scalar value read occurrence");
            }
        foreach (var result in erasedResults)
            switch (result)
            {
                case BirValueBlock block:
                    if (block.Type.OrDefault() is TypeNode.Tv) block.Type = ObjectType;
                    if (block.Sty.HasValue) block.Sty = ObjectType;
                    break;
                case BirConditional conditional:
                    if (conditional.Type.OrDefault() is TypeNode.Tv) conditional.Type = ObjectType;
                    break;
            }
        foreach (var slot in origins.Keys)
            if (slot.Type is TypeNode.Tv && slot.Declaration is BirVariable variable) variable.Type = ObjectType;
        foreach (var (use, slot) in uses)
            if (use is BirLocal local && origins.ContainsKey(slot)) local.Sty = ObjectType;
        foreach (var closure in captures)
            closure.VisitOwnChildSlots((field, _, slot) => {
                if (field != (closure is BirNewSuspendLambda ? "capValues" : "captures") || Origin(slot.Value) == null) return;
                slot.Replace(new BirCast { Type = ObjectType,
                    E = OptionalField<IBirExpression>.Of((IBirExpression)slot.Value.DeepClone()) });
            });
        foreach (var (cast, bound) in casts)
        {
            var target = IsObject(bound) ? ObjectType : new TypeNode.Nullable(bound);
            cast.Type = target;
            if (cast.Sty.HasValue) cast.Sty = target;
        }
        BirGraphValue ReplaceConstrained(BirGraphValue node)
        {
            if (node is IBirExecutablePayload || SourceNestedDeclaration(node)) return node;
            node.VisitOwnChildSlots((field, _, slot) => {
                if (!SourceSkip(field, node)) slot.Replace(ReplaceConstrained(slot.Value));
            });
            return node is BirConstrainedCall call && constrained.Contains(call) ? SourceInstance(call) : node;
        }
        declaration.VisitOwnChildSlots((field, _, slot) => {
            if (!SourceSkip(field, declaration)) slot.Replace(ReplaceConstrained(slot.Value));
        });
    }

    static bool SourceNestedDeclaration(BirGraphValue node) => node switch {
        BirFunctionDeclaration method => method.Params.HasValue && method.Body.HasValue,
        BirConstructor constructor => constructor.Params.HasValue && constructor.Body.HasValue,
        BirClosureDefinition closure => closure.Params.HasValue && closure.Body.HasValue,
        _ => false,
    };
    static bool SourceSkip(string field, BirGraphValue parent) =>
        field is "attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey
        || field == "body" && parent is BirNewClosure or BirNewSam or BirNewSuspendLambda;

    static TypeNode SourceValueType(BirGraphValue node)
    {
        TypeNode stamp = null, type = null, result = null;
        node.VisitOwnTypeFields((field, value) => {
            if (field == "sty") stamp = value;
            if (field == "type") type = value;
            if (field == "ret") result = value;
        });
        return stamp ?? type ?? result;
    }

    // Location exclusion reads the selected raw parameter vector. Unlike native
    // allocation, it does not close a callee's variables into the caller frame.
    static TypeNode[] SourceAddressParameters(BirGraphValue node)
    {
        var member = ForeignDeclarationFacts.Read(node)?.MemberRef;
        if (member?.ParameterTypes.OrDefault() is { } parameters) return parameters;
        return node switch {
            BirCallStatic call => call.ResolvedMemberParams.OrDefault() ?? call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault() ?? call.ArgTypes.OrDefault(),
            BirCallInstance call => call.ResolvedMemberParams.OrDefault() ?? call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault() ?? call.ArgTypes.OrDefault(),
            BirConstrainedCall call => call.ResolvedMemberParams.OrDefault() ?? call.Sig.OrDefault() ?? call.ArgTypes.OrDefault(),
            BirCallLocal call => call.Sig.OrDefault(),
            BirNativeStatic call => call.ResolvedMemberParams.OrDefault() ?? call.Sig.OrDefault() ?? call.ArgTypes.OrDefault(),
            BirNativeInstance call => call.ResolvedMemberParams.OrDefault() ?? call.Sig.OrDefault() ?? call.ArgTypes.OrDefault(),
            BirGenericStatic call => call.ResolvedMemberParams.OrDefault(),
            BirGenericInstance call => call.ResolvedMemberParams.OrDefault(),
            BirNew call => call.ResolvedMemberParams.OrDefault() ?? call.ArgTypes.OrDefault(),
            BirNewClr call => call.ResolvedMemberParams.OrDefault() ?? call.ArgTypes.OrDefault(),
            _ => null,
        };
    }

    static BirCallInstance SourceInstance(BirConstrainedCall call)
    {
        var selected = call.Iface.OrDefault()
            ?? throw new InvalidOperationException("Erased array receiver lost its selected constraint owner");
        return new BirCallInstance {
            OwnerType = BirGraphValue.CopyType(selected), Virtual = true, Method = call.Method,
            Recv = OptionalField<IBirExpression>.Of(new BirCast { Type = BirGraphValue.CopyType(selected),
                E = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)call.Recv.Value).DeepClone()) }),
            CalleeRet = call.CalleeRet, CalleeParams = call.CalleeParams, Sig = call.Sig, TypeArgs = call.TypeArgs,
            MemberRef = call.MemberRef, Ret = call.Ret, Args = call.Args, DeclarationId = call.DeclarationId,
            ArgTypes = call.ArgTypes, NullablePendingWitnesses = call.NullablePendingWitnesses,
            DotktDeclarationIdentityBound = call.DotktDeclarationIdentityBound, FunctionCallSignature = call.FunctionCallSignature,
            NullableWitnessCount = call.NullableWitnessCount, ResolvedMemberParams = call.ResolvedMemberParams,
            Sty = call.Sty, SuspendCall = call.SuspendCall, _capturedGenericArgument = call._capturedGenericArgument,
            _capturedGenericResult = call._capturedGenericResult, ResolvedMemberReturn = call.ResolvedMemberReturn,
        };
    }
}
