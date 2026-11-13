using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    sealed record InlineBoxRequest(string VarName, string RefName, TypeNode CellType, TypeNode Element,
        bool Synthesize, HashSet<string> CaptureNames);
    sealed record InlineBoxScope(BirGraphValue Declaration, BirGraphSlot Body, string LocalId);
    readonly Dictionary<string, string> refCellNames = new(StringComparer.Ordinal);
    readonly List<InlineBoxRequest> boxRequests = new();
    readonly Dictionary<BirCallLocal, HashSet<int>> boxedLocalCallArguments = new(ReferenceEqualityComparer.Instance);

    string RefCellNameFor(TypeNode element)
    {
        // Scalar type serialization is the established synthetic-name key, not a
        // graph hand-off. Preserve its complete classifier/context information.
        var key = element == null ? "null" : TypeNode.ToJson(element);
        if (refCellNames.TryGetValue(key, out var name)) return name;
        var mangled = new string(key.Select(value => char.IsLetterOrDigit(value) ? value : '_').ToArray());
        if (mangled.Length > 60) mangled = mangled.Substring(0, 60) + "_" + (uint)StringComparer.Ordinal.GetHashCode(key);
        name = "dotkt$inlmatref$Ref$" + mangled;
        refCellNames[key] = name;
        return name;
    }

    void RequestBox(BirInlineCapture capture, string captureName, TypeNode cellType, TypeNode element, bool synthesize)
    {
        var source = capture == null ? captureName : capture.Name.OrDefault();
        if (capture?.Value.OrDefault() is { } value)
            source = value is BirLocal local ? local.Name.OrDefault()
                : throw new InvalidOperationException("a mutable inline capture must retain its source local location");
        var refName = cellType is TypeNode.Fqn cell ? cell.Name
            : throw new InvalidOperationException("inline capture sharedCellType must name its cell declaration");
        var existing = boxRequests.FirstOrDefault(request => request.VarName == source && TypeNodeDataEquality.Equal(request.CellType, cellType));
        if (existing != null) existing.CaptureNames.Add(captureName);
        else boxRequests.Add(new InlineBoxRequest(source, refName, cellType, element, synthesize,
            new HashSet<string>(StringComparer.Ordinal) { captureName }));
    }

    static BirGraphValue RewriteBoxedCaptureInCarrier(BirGraphValue node, HashSet<string> boxed,
        Dictionary<string, TypeNode> cells, Dictionary<string, TypeNode> elements, string owner)
    {
        BirFieldGet CaptureField(string name) => new() {
            OwnerType = new TypeNode.Fqn(owner), Recv = OptionalField<IBirExpression>.Of(new BirThis()), Name = name,
        };
        if (node is BirLocal local && local.Name.HasValue && boxed.Contains(local.Name.Value))
            return new BirFieldGet { OwnerType = cells[local.Name.Value], Recv = OptionalField<IBirExpression>.Of(CaptureField(local.Name.Value)),
                Name = "v", Sty = elements.TryGetValue(local.Name.Value, out var element) ? OptionalField<TypeNode>.Of(element) : OptionalField<TypeNode>.Null };
        if (node is BirSetLocal write && write.Name.HasValue && boxed.Contains(write.Name.Value))
            return new BirSetField {
                OwnerType = cells[write.Name.Value], Recv = OptionalField<IBirExpression>.Of(CaptureField(write.Name.Value)), Name = "v",
                Value = write.Value.HasValue ? OptionalField<IBirExpression>.Of((IBirExpression)RewriteBoxedCaptureInCarrier(
                    (BirGraphValue)write.Value.Value, boxed, cells, elements, owner)) : write.Value,
            };
        WithoutSyntheticDeclarationSlots(node, slot => slot.Replace(RewriteBoxedCaptureInCarrier(slot.Value, boxed, cells, elements, owner)));
        return node;
    }

    internal void FinishCaptureBoxing()
    {
        if (boxRequests.Count == 0) return;
        var registry = Pass.File.RefTypes.OrEmpty().ToList();
        var present = registry.Where(value => value.Name.HasValue).Select(value => value.Name.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var request in boxRequests)
            if (request.Synthesize && present.Add(request.RefName))
                registry.Add(new BirRefTypeDeclaration { Name = request.RefName, Elem = request.Element, TypeParams = Array.Empty<BirTypeParameter>() });
        Pass.File.RefTypes = registry.ToArray();

        var scopes = CollectBoxScopes();
        var pending = new Queue<(int Scope, InlineBoxRequest Request)>();
        for (var scope = 0; scope < scopes.Count; scope++)
            foreach (var request in boxRequests)
                foreach (var source in MaterializedCaptureSources(scopes[scope].Body.Value, request))
                    pending.Enqueue((scope, request with { VarName = source }));
        var completed = new HashSet<(int Scope, string Name, string Cell)>();
        var rewrites = new List<(int Scope, InlineBoxRequest Request)>();
        while (pending.TryDequeue(out var entry))
        {
            var request = entry.Request;
            var scope = scopes[entry.Scope];
            if (!completed.Add((entry.Scope, request.VarName, TypeNode.ToJson(request.CellType)))) continue;
            var parameters = scope.Declaration switch {
                BirFunctionDeclaration method => method.Params.OrEmpty(), BirConstructor constructor => constructor.Params.OrEmpty(),
                _ => Array.Empty<BirParameter>(),
            };
            // Other scope owners have lambda parameters, never local-function
            // forwarding slots; matching one remains an invalid lexical location.
            var lambdaParameters = scope.Declaration switch {
                BirClosureDefinition closure => closure.Params.OrEmpty(), BirNewSuspendLambda suspend => suspend.Params.OrEmpty(),
                _ => Array.Empty<BirLambdaParameter>(),
            };
            var index = Array.FindIndex(parameters, parameter => parameter.Name.OrDefault() == request.VarName);
            if (index < 0 && lambdaParameters.Any(parameter => parameter.Name.OrDefault() == request.VarName))
                throw new InvalidOperationException("shared inline capture is missing its lexical parameter location");
            if (index >= 0)
            {
                if (scope.LocalId == null || !parameters[index].SharedCellType.HasValue)
                    throw new InvalidOperationException("shared inline capture is missing its lexical parameter location");
                parameters[index].Type = request.CellType;
                for (var caller = 0; caller < scopes.Count; caller++)
                {
                    var callerIndex = caller;
                    void VisitCalls(BirGraphValue node)
                    {
                        if (node == null || node is BirLocalFunction) return;
                        if (node is BirCallLocal call && call.Id.OrDefault() == scope.LocalId)
                        {
                            if (!call.Args.HasValue || index >= call.Args.Value.Length || call.Args.Value[index] == null)
                                throw new InvalidOperationException("shared local-function capture must pass its source location");
                            var arguments = call.Args.Value;
                            var argument = arguments[index];
                            var typeArguments = call.TypeArgs.OrEmpty();
                            var cell = InstantiateBoxType(request.CellType, typeArguments);
                            var element = InstantiateBoxType(request.Element, typeArguments);
                            call.Sig.Value[index] = cell;
                            if (argument is BirFieldGet read && read.Name.OrDefault() == "v"
                                && TypeNodeDataEquality.Equal(read.OwnerType.OrDefault(), cell) && read.Recv.HasValue)
                                arguments[index] = Copy(read.Recv.Value);
                            else
                            {
                                if (argument is not BirLocal source || !source.Name.HasValue)
                                    throw new InvalidOperationException("shared local-function capture must pass its source location");
                                if (!boxedLocalCallArguments.TryGetValue(call, out var slots)) boxedLocalCallArguments[call] = slots = new();
                                slots.Add(index);
                                pending.Enqueue((callerIndex, new InlineBoxRequest(source.Name.Value, request.RefName, cell, element, false, new())));
                            }
                        }
                        VisitBoxChildren(node, slot => VisitCalls(slot.Value));
                    }
                    VisitCalls(scopes[caller].Body.Value);
                }
            }
            rewrites.Add(entry);
        }
        // Resolve the complete forwarding graph before any caller reads change.
        foreach (var entry in rewrites)
        {
            var request = entry.Request;
            var scope = scopes[entry.Scope];
            if (BoxUnsafeReason(scope.Body.Value, request.VarName, request.RefName, request.Element) is string reason)
                throw new NotSupportedException($"inline splice: cannot ref-cell-box captured var '{request.VarName}' — {reason}");
            scope.Body.Replace(BoxVarInBody(scope.Body.Value, request.VarName, request.CellType, request.Element));
        }
    }

    List<InlineBoxScope> CollectBoxScopes()
    {
        var scopes = new List<InlineBoxScope>();
        BirGraphSlot Body(Func<OptionalField<IBirStatement[]>> read, Action<IBirStatement[]> write) => new(
            () => BodyGraph(read()), value => write(((BirValueBlock)value).Stmts.OrEmpty()));
        void Add(BirGraphValue declaration, BirGraphSlot body, string local = null)
        {
            scopes.Add(new InlineBoxScope(declaration, body, local));
            CollectLocals(body.Value);
        }
        void CollectLocals(BirGraphValue node)
        {
            if (node == null) return;
            if (node is BirLocalFunction local && local.Decl.HasValue && local.Decl.Value.Body.HasValue)
            {
                var declaration = local.Decl.Value;
                Add(declaration, Body(() => declaration.Body, value => declaration.Body = value), local.Id.OrDefault());
                return;
            }
            if (node is BirNewClosure { SynthClass.HasValue: true } closure && closure.SynthClass.Value.Body.HasValue)
            {
                var synthetic = closure.SynthClass.Value;
                Add(synthetic, Body(() => synthetic.Body, value => synthetic.Body = value));
            }
            if (node is BirNewSuspendLambda suspend && suspend.Body.HasValue)
                Add(suspend, Body(() => suspend.Body, value => suspend.Body = value));
            VisitBoxChildren(node, slot => CollectLocals(slot.Value));
        }
        foreach (var method in Pass.File.Methods.OrEmpty())
            if (method.Body.HasValue) Add(method, Body(() => method.Body, value => method.Body = value));
        foreach (var type in Pass.File.Types.OrEmpty())
        {
            foreach (var method in type.Methods.OrEmpty())
                if (method.Body.HasValue) Add(method, Body(() => method.Body, value => method.Body = value));
            foreach (var constructor in type.Ctors.OrEmpty())
                if (constructor.Body.HasValue) Add(constructor, Body(() => constructor.Body, value => constructor.Body = value));
        }
        void Initializers(BirFieldDeclaration[] fields)
        {
            foreach (var field in fields)
                if (field.Init.HasValue) Add(field, new BirGraphSlot(() => (BirGraphValue)field.Init.Value,
                    value => field.Init = OptionalField<IBirExpression>.Of((IBirExpression)value)));
        }
        Initializers(Pass.File.Fields.OrEmpty());
        foreach (var type in Pass.File.Types.OrEmpty()) Initializers(type.Fields.OrEmpty());
        return scopes;
    }

    // Boxing deliberately does not enter nested lexical functions, synthetic
    // bodies, or a state machine's body. Their construction operands stay live.
    static void VisitBoxChildren(BirGraphValue node, Action<BirGraphSlot> visit)
    {
        if (node is BirNewSuspendLambda suspend)
        {
            foreach (var descriptor in suspend.Captures.OrEmpty()) descriptor.VisitChildSlots(visit);
            if (suspend.CapValues.HasValue)
                for (var index = 0; index < suspend.CapValues.Value.Length; index++)
                {
                    var at = index;
                    if (suspend.CapValues.Value[at] != null) visit(new BirGraphSlot(() => (BirGraphValue)suspend.CapValues.Value[at],
                        value => suspend.CapValues.Value[at] = (IBirExpression)value));
                }
            foreach (var parameter in suspend.Params.OrEmpty()) visit(new BirGraphSlot(() => parameter,
                _ => throw new InvalidOperationException("Boxing cannot replace a lambda parameter")));
            foreach (var parameter in suspend.TypeParams.OrEmpty()) visit(new BirGraphSlot(() => parameter,
                _ => throw new InvalidOperationException("Boxing cannot replace a generic parameter")));
            return;
        }
        WithoutSyntheticDeclarationSlots(node, visit);
    }

    static IEnumerable<string> MaterializedCaptureSources(BirGraphValue node, InlineBoxRequest request)
    {
        if (node == null || node is BirLocalFunction) yield break;
        var fields = node switch {
            BirNewClosure closure => closure.SynthClass.OrDefault()?.Fields.OrEmpty().Select(value => (value.Name.OrDefault(), value.Type.OrDefault())),
            BirNewSam sam => sam.SynthClass.OrDefault()?.Fields.OrEmpty().Select(value => (value.Name.OrDefault(), value.Type.OrDefault())),
            BirNewSuspendLambda suspend => suspend.Captures.OrEmpty().Select(value => (value.Name.OrDefault(), value.Type.OrDefault())),
            _ => null,
        };
        var values = node switch {
            BirNewClosure closure => closure.Captures.OrDefault(), BirNewSam sam => sam.Captures.OrDefault(),
            BirNewSuspendLambda suspend => suspend.CapValues.OrDefault(), _ => null,
        };
        if (fields != null)
        {
            var index = 0;
            foreach (var (name, type) in fields)
            {
                if (name != null && request.CaptureNames.Contains(name) && type is TypeNode.Fqn cell && cell.Name == request.RefName)
                {
                    var value = values != null && index < values.Length ? values[index] : null;
                    if (value is BirLocal { Name.HasValue: true } local) yield return local.Name.Value;
                    else if (value == null && node is BirNewSuspendLambda) yield return name;
                }
                index++;
            }
        }
        var children = new List<BirGraphValue>(); VisitBoxChildren(node, slot => children.Add(slot.Value));
        foreach (var child in children) foreach (var source in MaterializedCaptureSources(child, request)) yield return source;
    }

    static string BoxUnsafeReason(BirGraphValue body, string name, string refName, TypeNode element)
    {
        var declarations = 0; string reason = null;
        void Walk(BirGraphValue node)
        {
            if (node == null || reason != null || node is BirLocalFunction) return;
            if (node is BirVariable local && local.Name.OrDefault() == name)
            {
                declarations++;
                if (!TypeNodeDataEquality.Equal(local.Type.OrDefault(), element))
                    reason = "a same-named local of a different type shadows it (type-ambiguous rewrite)";
            }
            IEnumerable<(string Name, TypeNode Type)> descriptors = node switch {
                BirInlineLambda lambda => lambda.Captures.OrEmpty().Select(value => (value.Name.OrDefault(), value.Type.OrDefault())),
                BirNewSuspendLambda suspend => suspend.Captures.OrEmpty().Select(value => (value.Name.OrDefault(), value.Type.OrDefault())),
                BirClosureDefinition closure => closure.Fields.OrEmpty().Select(value => (value.Name.OrDefault(), value.Type.OrDefault())),
                IBirTypeDeclaration type => type.Fields.OrEmpty().Select(value => (value.Name.OrDefault(), value.Type.OrDefault())),
                _ => Array.Empty<(string, TypeNode)>(),
            };
            foreach (var descriptor in descriptors)
                if (descriptor.Name == name && descriptor.Type != null
                    && !(descriptor.Type is TypeNode.Fqn cell && cell.Name == refName))
                    reason = "a second closure/suspend-lambda captures it (would receive the ref-cell where the plain value is expected)";
            VisitBoxChildren(node, slot => Walk(slot.Value));
        }
        Walk(body);
        return reason ?? (declarations > 1 ? "it is declared more than once in the method (shadowing — ambiguous which binder to box)" : null);
    }

    BirGraphValue BoxVarInBody(BirGraphValue node, string name, TypeNode cell, TypeNode element)
    {
        if (node == null || node is BirLocalFunction) return node;
        bool IsRead(IBirExpression expression) => expression is BirLocal readLocal && readLocal.Name.OrDefault() == name;
        BirFieldGet Read() => new() { OwnerType = cell, Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = name }), Name = "v",
            Sty = OptionalField<TypeNode>.Of(element) };
        if (node is BirLocal local && local.Name.OrDefault() == name) return Read();
        if (node is BirSetLocal write && write.Name.OrDefault() == name)
            return new BirSetField { OwnerType = cell, Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = name }), Name = "v",
                Value = write.Value.HasValue ? OptionalField<IBirExpression>.Of((IBirExpression)BoxVarInBody((BirGraphValue)write.Value.Value, name, cell, element)) : write.Value };
        if (node is BirVariable variable && variable.Name.OrDefault() == name)
        {
            var argument = Copy(variable.Init.OrDefault()) ?? new BirDefaultValue { Type = element };
            argument = (IBirExpression)BoxVarInBody((BirGraphValue)argument, name, cell, element);
            variable.Type = cell;
            variable.Init = OptionalField<IBirExpression>.Of(new BirNew { Type = cell, ArgTypes = new[] { element }, Args = new[] { argument } });
            return variable;
        }
        void Captures(IBirExpression[] values)
        {
            if (values == null) return;
            for (var index = 0; index < values.Length; index++)
                if (!IsRead(values[index])) values[index] = (IBirExpression)BoxVarInBody((BirGraphValue)values[index], name, cell, element);
        }
        if (node is BirNewClosure closure) { Captures(closure.Captures.OrDefault()); return node; }
        if (node is BirNewSam sam) { Captures(sam.Captures.OrDefault()); return node; }
        if (node is BirNewSuspendLambda suspend)
        {
            Captures(suspend.CapValues.OrDefault());
            foreach (var descriptor in suspend.Captures.OrEmpty()) BoxVarInBody(descriptor, name, cell, element);
            foreach (var parameter in suspend.Params.OrEmpty()) BoxVarInBody(parameter, name, cell, element);
            foreach (var parameter in suspend.TypeParams.OrEmpty()) BoxVarInBody(parameter, name, cell, element);
            return node;
        }
        if (node is BirCallLocal call && boxedLocalCallArguments.TryGetValue(call, out var slots))
        {
            var arguments = call.Args.OrEmpty();
            for (var index = 0; index < arguments.Length; index++)
                if (!(slots.Contains(index) && IsRead(arguments[index])))
                    arguments[index] = (IBirExpression)BoxVarInBody((BirGraphValue)arguments[index], name, cell, element);
            // A callLocal has no other executable child channel.
            return call;
        }
        WithoutSyntheticDeclarationSlots(node, slot => slot.Replace(BoxVarInBody(slot.Value, name, cell, element)));
        return node;
    }

    static TypeNode InstantiateBoxType(TypeNode type, TypeNode[] arguments)
    {
        var holder = (BirParameter)TypedExecutableFrames.Substitute(new BirParameter { Type = type }, arguments, arguments.Length);
        return holder.Type.OrDefault();
    }
}
