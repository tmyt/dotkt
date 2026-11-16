using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExistentialCaptureAlignment
{
    sealed record CaptureUse(BirGraphValue Construction, int Position, TypeNode InitialType, string EnclosingType)
    {
        IBirExpression[] Values => Construction switch {
            BirNewClosure value => value.Captures.Value, BirNewSam value => value.Captures.Value,
            _ => throw new InvalidOperationException("Capture use lost its construction"),
        };
        internal BirGraphValue Expression => (BirGraphValue)Values[Position];
        internal void Replace(IBirExpression expression) => Values[Position] = expression;
    }

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> files,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs,
        ValueTypeOracle isValue = null, GenericRepresentationPolicy policy = null,
        Action<string, string, int, int, TypeNode> recordStorage = null)
    {
        isValue ??= _ => false;
        var semanticByPhysical = localExistentialOwners.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        // This is the current physical membership, not an original source index.
        // Preserve last-writer definitions and the corresponding frame overlay.
        var definitions = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var type in file.Types.OrEmpty())
                if (type.Name.HasValue) definitions[type.Name.Value] = type;
        var frames = refs.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (name, definition) in definitions)
            if (NullableRepresentationViews.TypeFacts((BirGraphValue)definition)?.NullableFrame is { } frame) frames[name] = frame;
        foreach (var file in files) Apply(file, semanticByPhysical, refs, definitions, frames, isValue, policy, recordStorage);
    }

    static void Apply(BirFileDeclaration file, IReadOnlyDictionary<string, string> semanticByPhysical,
        ReferenceMetadataIndex refs, IReadOnlyDictionary<string, IBirTypeDeclaration> definitions,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy, Action<string, string, int, int, TypeNode> recordStorage)
    {
        var uses = new Dictionary<(string Closure, int Position), List<CaptureUse>>();
        void PrepareSuspend(BirNewSuspendLambda lambda) => ErasedElementValues.ApplySuspendBody(lambda, refs, definitions, frames, isValue, policy);
        foreach (var method in file.Methods.OrEmpty()) VisitDeclaration(method, null, uses, semanticByPhysical, refs, PrepareSuspend);
        foreach (var type in file.Types.OrEmpty())
        {
            var enclosing = type.Name.HasValue && definitions.TryGetValue(type.Name.Value, out var current)
                && ReferenceEquals(current, type) ? type.Name.Value : null;
            foreach (var method in type.Methods.OrEmpty()) VisitDeclaration(method, enclosing, uses, semanticByPhysical, refs, PrepareSuspend);
            foreach (var constructor in type.Ctors.OrEmpty()) VisitDeclaration(constructor, enclosing, uses, semanticByPhysical, refs, PrepareSuspend);
        }
        var dependents = new Dictionary<string, HashSet<(string Closure, int Position)>>(StringComparer.Ordinal);
        foreach (var (key, captureUses) in uses)
            foreach (var use in captureUses)
                if (use.EnclosingType is { } owner)
                {
                    if (!dependents.TryGetValue(owner, out var set)) dependents[owner] = set = new();
                    set.Add(key);
                }
        bool IsObject(TypeNode type) => type != null && BirTypeLowering.SamePhysicalSlotType(type,
            new TypeNode.Fqn("object"), refs.Aliases, isValue, refs.PhysicalTypeNames,
            returnPosition: false, nullableFrames: frames);
        TypeNode Current(CaptureUse use)
        {
            var current = PhysicalType(use.Expression, new Dictionary<string, TypeNode>());
            return IsObject(current) || current is TypeNode.Fqn named && TrySemanticOwner(named.Name, semanticByPhysical, refs, out _)
                ? current : use.InitialType;
        }
        var pending = new HashSet<(string Closure, int Position)>(uses.Keys);
        var queue = new Queue<(string Closure, int Position)>(uses.Keys);
        while (queue.TryDequeue(out var key))
        {
            pending.Remove(key);
            var (closureName, position) = key;
            var captureUses = uses[key];
            var types = captureUses.Select(Current).ToArray();
            var erased = types.Any(IsObject);
            var carriers = types.OfType<TypeNode.Fqn>()
                .Where(type => TrySemanticOwner(type.Name, semanticByPhysical, refs, out _)).Distinct().ToArray();
            if (!erased && carriers.Length == 0) continue;
            if (!erased && carriers.Length != 1)
                throw new InvalidOperationException($"bir2cir: generated closure '{closureName}' capture {position} has conflicting existential carriers");
            if (!definitions.TryGetValue(closureName, out var closure) || !closure.Generated.OrDefault()
                || position >= closure.Fields.OrEmpty().Length || closure.Fields.Value[position] is not { } field
                || !field.Name.HasValue || !field.Type.HasValue)
                throw new InvalidOperationException($"bir2cir: existential capture {position} has no exact generated closure storage in '{closureName}'");
            var fieldName = field.Name.Value;
            var fieldType = field.Type.Value;
            string semanticOwner = null;
            var raw = erased && fieldType is TypeNode.Tv;
            var carrier = raw ? new TypeNode.Fqn("object") : carriers.FirstOrDefault();
            if (carrier == null || fieldType.Equals(carrier)) continue;
            if (!raw && (!TrySemanticOwner(carrier.Name, semanticByPhysical, refs, out semanticOwner)
                || fieldType is not TypeNode.Fqn { Args.Length: > 0 } logical || logical.Name != semanticOwner)) continue;
            if (closure.Ctors.OrEmpty() is not [var constructor] || position >= constructor.Params.OrEmpty().Length
                || constructor.Params.Value[position] is not { } parameter || parameter.Name.OrDefault() != fieldName)
                throw new InvalidOperationException($"bir2cir: generated closure '{closureName}' capture '{fieldName}' has no exact constructor slot");
            field.Type = carrier;
            parameter.Type = carrier;
            recordStorage?.Invoke(closureName, fieldName, position, constructor.Params.Value.Length, carrier);
            if (raw)
            {
                var source = NullableRepresentationViews.TypeFacts((BirGraphValue)closure)?.NullableFrame?.SemanticVariable((TypeNode.Tv)fieldType) ?? fieldType;
                if (!field.KotlinType.HasValue) field.KotlinType = source;
                if (!parameter.KotlinType.HasValue) parameter.KotlinType = source;
            }
            RetypeFieldUses((BirGraphValue)closure, closureName, fieldName, carrier);
            foreach (var use in captureUses)
                if (raw)
                {
                    if (use.Expression is not BirCast cast || !IsObject(cast.Type.OrDefault()))
                        use.Replace(new BirCast { Type = carrier, E = OptionalField<IBirExpression>.Of((IBirExpression)use.Expression) });
                }
                else RetypeCaptureBoundary(use.Expression, semanticOwner, carrier);
            if (raw) ErasedElementValues.ApplyType(closure, refs, definitions, frames, isValue, policy);
            if (dependents.TryGetValue(closureName, out var affected))
                foreach (var consumer in affected)
                    if (pending.Add(consumer)) queue.Enqueue(consumer);
        }
    }

    static void VisitDeclaration(BirGraphValue declaration, string enclosing,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<BirNewSuspendLambda> prepareSuspend)
    {
        var body = declaration switch { BirFunctionDeclaration method => method.Body.OrDefault(),
            BirConstructor constructor => constructor.Body.OrDefault(), _ => null };
        if (body == null) return;
        var vars = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        declaration.VisitSourceParameters((name, type) => vars[name] = type);
        VisitSequence(body, vars, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
    }

    static void VisitSequence(IEnumerable<IBirStatement> statements, IReadOnlyDictionary<string, TypeNode> vars, string enclosing,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<BirNewSuspendLambda> prepareSuspend)
    {
        var block = new Dictionary<string, TypeNode>(vars, StringComparer.Ordinal);
        foreach (var statement in statements) Visit((BirGraphValue)statement, block, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
    }

    static void Visit(BirGraphValue node, Dictionary<string, TypeNode> vars, string enclosing,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<BirNewSuspendLambda> prepareSuspend)
    {
        if (node == null || node is IBirExecutablePayload) return;
        if (node is BirVariable variable)
        {
            if (variable.Init.HasValue) Visit((BirGraphValue)variable.Init.Value, vars, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
            if (variable.Name.HasValue && variable.Type.HasValue) vars[variable.Name.Value] = variable.Type.Value;
            return;
        }
        if (node is BirNewSuspendLambda suspend)
        {
            AlignSuspendCaptures(suspend, vars, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
            return;
        }
        var closure = node switch { BirNewClosure value => value.ClosureType.OrDefault() as TypeNode.Fqn,
            BirNewSam value => value.SamType.OrDefault() as TypeNode.Fqn, _ => null };
        var captures = node switch { BirNewClosure value => value.Captures.OrDefault(), BirNewSam value => value.Captures.OrDefault(), _ => null };
        if (closure != null && captures != null)
        {
            for (var position = 0; position < captures.Length; position++)
                if (captures[position] is BirGraphValue capture)
                {
                    var key = (closure.Name, position);
                    if (!uses.TryGetValue(key, out var list)) uses[key] = list = new();
                    list.Add(new(node, position, PhysicalType(capture, vars), enclosing));
                }
            foreach (var capture in captures) Visit((BirGraphValue)capture, vars, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
            return;
        }
        if (node is BirInlineLambda or BirNewDelegate) return;
        node.VisitSourceChildren((_, role, child) => {
            if (!NullableRepresentationViews.Frozen(role)) Visit(child.Value, vars, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
        }, region => {
            VisitSequence(region.Statements, vars, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
            // Each old statement vector owns its environment; Result is not a
            // continuation of that vector in this collector (PhysicalType is).
            if (region.Result is { } result) Visit(result.Value, vars, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
        });
    }

    static void AlignSuspendCaptures(BirNewSuspendLambda node, IReadOnlyDictionary<string, TypeNode> outerVars, string enclosing,
        Dictionary<(string Closure, int Position), List<CaptureUse>> uses,
        IReadOnlyDictionary<string, string> semanticByPhysical, ReferenceMetadataIndex refs, Action<BirNewSuspendLambda> prepareSuspend)
    {
        var nested = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var rawChanged = false;
        for (var position = 0; position < node.Captures.OrEmpty().Length; position++)
        {
            var capture = node.Captures.Value[position];
            if (!capture.Name.HasValue || !capture.Type.HasValue) continue;
            var name = capture.Name.Value;
            var declared = capture.Type.Value;
            var value = node.CapValues.HasValue && position < node.CapValues.Value.Length
                ? node.CapValues.Value[position] as BirGraphValue : null;
            var physical = value != null ? PhysicalType(value, outerVars) : outerVars.GetValueOrDefault(name);
            if (declared is TypeNode.Tv && physical != null && BirTypeLowering.SamePhysicalSlotType(physical,
                new TypeNode.Fqn("object"), refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames,
                returnPosition: false, nullableFrames: refs.NullableTypeFrames))
            {
                if (!capture.KotlinType.HasValue) capture.KotlinType = declared;
                capture.Type = declared = new TypeNode.Fqn("object");
                rawChanged = true;
            }
            else if (physical is TypeNode.Fqn carrier && TrySemanticOwner(carrier.Name, semanticByPhysical, refs, out var semanticOwner)
                && declared is TypeNode.Fqn { Args.Length: > 0 } logical && logical.Name == semanticOwner)
            {
                capture.Type = declared = carrier;
                if (value != null) RetypeCaptureBoundary(value, semanticOwner, carrier);
            }
            nested[name] = declared;
        }
        if (rawChanged) prepareSuspend(node);
        foreach (var parameter in node.Params.OrEmpty())
            if (parameter.Name.HasValue && parameter.Type.HasValue) nested[parameter.Name.Value] = parameter.Type.Value;
        if (node.Body.HasValue) VisitSequence(node.Body.Value, nested, enclosing, uses, semanticByPhysical, refs, prepareSuspend);
    }

    static TypeNode PhysicalType(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> vars)
    {
        if (node is BirLocal local && local.Name.HasValue && vars.TryGetValue(local.Name.Value, out var type)) return type;
        if (node is BirValueBlock block)
        {
            var nested = new Dictionary<string, TypeNode>(vars, StringComparer.Ordinal);
            foreach (var statement in block.Stmts.OrEmpty())
                if (statement is BirVariable variable && variable.Name.HasValue && variable.Type.HasValue) nested[variable.Name.Value] = variable.Type.Value;
            return PhysicalType(block.Result.OrDefault() as BirGraphValue, nested);
        }
        if (node is BirConditional conditional)
        {
            var thenType = PhysicalType(conditional.Then.OrDefault() as BirGraphValue, vars);
            var elseType = PhysicalType(conditional.Else.OrDefault() as BirGraphValue, vars);
            if (NodeType.IsNothing(thenType)) return elseType;
            if (NodeType.IsNothing(elseType)) return thenType;
            return thenType != null && thenType.Equals(elseType) ? thenType : null;
        }
        return BirNodeType.Of(node as BirGraphNode, child => PhysicalType(child, vars));
    }

    static void RetypeCaptureBoundary(BirGraphValue expression, string semanticOwner, TypeNode.Fqn carrier)
        => expression.RewriteOwnTypeFieldsInPlace((field, _, type) => field is "type" or "sty"
            && type is TypeNode.Fqn { Args.Length: > 0 } logical && logical.Name == semanticOwner ? carrier : type);

    static void RetypeFieldUses(BirGraphValue node, string closureName, string fieldName, TypeNode.Fqn carrier)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirFieldGet field && field.Name.OrDefault() == fieldName
            && field.OwnerType.OrDefault() is TypeNode.Fqn owner && owner.Name == closureName)
        {
            field.Sty = carrier;
            if (field.MemberType.HasValue) field.MemberType = carrier;
        }
        node.VisitOwnChildSlots((_, role, slot) => { if (!NullableRepresentationViews.Frozen(role)) RetypeFieldUses(slot.Value, closureName, fieldName, carrier); });
    }
}
