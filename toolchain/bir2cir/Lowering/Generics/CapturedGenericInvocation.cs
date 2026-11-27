using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A Kotlin capture is not its upper bound's CLR generic construction. Preserve that source fact before
// representation lowering, then forward the already-selected declaration in the captured runtime frame.
// Neither the declaration ABI nor managed-reference storage is changed by this operation.
static partial class CapturedGenericInvocation
{
    internal const string ArgumentKey = "_capturedGenericArgument";
    const string ResultKey = "_capturedGenericResult";
    const string TargetKey = "_capturedGenericTarget";
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    static int _next;
    static readonly Dictionary<string, TypeNode[]> _parameterWitnesses = new(StringComparer.Ordinal);
    static readonly Dictionary<string, NullableRepresentationFrame> _methodWitnessFrames = new(StringComparer.Ordinal);

    static TypeNode.Fqn CaptureOwner(TypeNode type) => type switch {
        TypeNode.Nullable n => CaptureOwner(n.Of),
        TypeNode.Oblivious o => CaptureOwner(o.Of),
        TypeNode.Mod m => CaptureOwner(m.Of),
        TypeNode.Fqn f => f,
        _ => null,
    };

    public static BirFileDeclaration[] PreserveSourceFacts(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs,
        SourceDeclarationSignatures signatures = null)
    {
        _next = 0;
        _parameterWitnesses.Clear();
        _methodWitnessFrames.Clear();
        var locals = new Dictionary<string, IReadOnlyList<BirTypeParameter>>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(root => root.Types.OrEmpty()))
        {
            var (name, parameters) = owner switch {
                BirClassDefinition value => (value.Name, value.TypeParams),
                BirInterfaceDefinition value => (value.Name, value.TypeParams),
                BirEnumDefinition value => (value.Name, value.TypeParams),
                _ => throw new InvalidOperationException("Unaccounted captured-source owner: " + owner.GetType().Name),
            };
            if (name.HasValue) locals[name.Value] = parameters.OrDefault();
        }
        IReadOnlyDictionary<string, SupertypeGraph.Def> hierarchy = null;
        string OwnerIdentity(string name) => locals.ContainsKey(name) ? name : refs?.RecordedPhysicalTypeName(name) ?? name;
        BirFileDeclaration currentFile = null;
        Dictionary<string, BirFunctionDeclaration> lexical = null;
        return roots.Select(root => {
            currentFile = root; lexical = null;
            return (BirFileDeclaration)root.Rewrite(Visit);
        }).ToArray();

        BirFunctionDeclaration Lexical(string id)
        {
            if (lexical == null)
            {
                lexical = new(StringComparer.Ordinal);
                currentFile.Visit(n => { if (n is BirLocalFunction f) lexical.Add(f.Id.Value, f.Decl.Value); });
            }
            return lexical.GetValueOrDefault(id);
        }

        CapturedGenericArgument[] Capture(IBirExpression[] arguments, TypeNode[] signature)
        {
            // Use-site projections hide the invariant owner's actual argument:
            // neither an out bound nor an in bound is the CLR construction.
            static bool NeedsCapture(TypeNode type) => type is TypeNode.Star or TypeNode.Projection;
            // An NRT-oblivious T! still denotes the same CLR generic slot as T.
            // Do not look through nullable or constructed types: their storage need not be T.
            static bool IsMethodSlot(TypeNode type) => type is TypeNode.Oblivious oblivious
                ? IsMethodSlot(oblivious.Of) : type is TypeNode.Tv { Scope: "method" };
            var captures = new List<CapturedGenericArgument>();
            for (var index = 0; index < arguments.Length && index < signature.Length; index++)
            {
                if (CaptureOwner(signature[index]) is not { Args: { } formalArgs } formal
                    || !formalArgs.Any(IsMethodSlot)
                    || CaptureOwner(BirNodeType.Of(arguments[index] as BirGraphNode)) is not { Args: { } actualArgs } actual
                    || !actualArgs.Any(NeedsCapture)) continue;
                var formalOwner = OwnerIdentity(formal.Name);
                actualArgs = View(actual, formalOwner);
                if (actualArgs == null) continue;
                if (actualArgs.Length != formalArgs.Length) continue;
                var local = locals.TryGetValue(formal.Name, out var localParameters);
                var parameters = local ? localParameters : refs.OwnerTypeParametersInPhysicalFrame(formal.Name);
                var frame = !local && refs.NullableTypeFrames.TryGetValue(formal.Name, out var referencedFrame)
                    ? referencedFrame : null;
                if (parameters == null || (frame?.SourceArity ?? parameters.Count) != formalArgs.Length) continue;
                var positions = Enumerable.Range(0, formalArgs.Length).Where(slot =>
                        IsMethodSlot(formalArgs[slot])
                        && NeedsCapture(actualArgs[slot])
                        && parameters[frame?.SourcePosition(slot) ?? slot].Variance.OrDefault() is not ("in" or "out")).ToArray();
                if (positions.Length != 0)
                {
                    var row = new CapturedGenericArgument { Argument = index, OwnerPositions = positions };
                    if (AppliedPositions(arguments[index], formalOwner, positions) is { Length: > 0 } applied)
                        row.AppliedPositions = applied;
                    captures.Add(row);
                }
            }
            return captures.ToArray();
        }

        // A generic producer returning List<T> applies its invocation's T. It
        // does not discover T from the resulting list. Keep an existential
        // return (List<*>) and a call which itself recovered T as real evidence.
        int[] AppliedPositions(IBirExpression operand, string formalOwner, int[] positions)
        {
            if (operand is BirThrowExpression) return positions; // no value reaches the call
            if (operand is BirValueBlock block)
            {
                if (block.Result.OrDefault() is not BirLocal result)
                    return AppliedPositions(block.Result.OrDefault(), formalOwner, positions);
                // A block-local result location joins its reaching writes, e.g.
                // the normal and exceptional exits of a try expression. A
                // borrowed, shared or shadowed location is not such a join.
                var name = result.Name.OrDefault();
                if (!block.Stmts.OrEmpty().OfType<BirVariable>().Any(v => v.Name.OrDefault() == name)) return null;
                var remaining = positions;
                int declarations = 0, writes = 0;
                void Written(IBirExpression value)
                {
                    if (value != null && remaining is { Length: > 0 })
                    { writes++; remaining = AppliedPositions(value, formalOwner, remaining); }
                }
                foreach (var statement in block.Stmts.Value)
                    ((BirGraphValue)statement).Visit(n => {
                        switch (n)
                        {
                            case BirVariable v when v.Name.OrDefault() == name: declarations++; Written(v.Init.OrDefault()); break;
                            case BirSetLocal s when s.Name.OrDefault() == name: Written(s.Value.OrDefault()); break;
                            case BirLocal l when l.Name.OrDefault() == name:
                            case BirByRefStore b when b.Local.OrDefault() == name: remaining = null; break;
                        }
                    });
                return declarations == 1 && writes != 0 ? remaining : null;
            }
            if (operand is BirConditional branch)
            {
                // Only the selected result contributes a value; neither the
                // condition nor statements evaluated for effects are witnesses.
                var left = AppliedPositions(branch.Then.OrDefault(), formalOwner, positions);
                return left is { Length: > 0 } ? AppliedPositions(branch.Else.OrDefault(), formalOwner, left) : null;
            }
            var (id, typeArgs, inputs, signature, actuals) = operand switch {
                BirCallStatic s when !s.SuspendCall.OrDefault() => (s.DeclarationId.OrDefault(), s.TypeArgs.OrDefault(), s._capturedGenericArgument.OrDefault(), s.Sig.OrDefault() ?? s.ShapeTypes.OrDefault(), s.Args.OrDefault()),
                BirCallInstance m when !m.SuspendCall.OrDefault() => (m.DeclarationId.OrDefault(), m.TypeArgs.OrDefault(), m._capturedGenericArgument.OrDefault(), m.Sig.OrDefault() ?? m.ShapeTypes.OrDefault(), m.Args.OrDefault()),
                BirCallLocal l => (l.Id.OrDefault(), l.TypeArgs.OrDefault(), null, l.Sig.OrDefault(), l.Args.OrDefault()),
                _ => default,
            };
            if (id == null || typeArgs is not { Length: > 0 }) return null;
            var ret = operand is BirCallLocal ? Lexical(id)?.Ret.OrDefault() :
                signatures != null && signatures.TryGetValue(id, out var local)
                ? local.Return : refs?.DeclarationSourceReturn(id);
            if (CaptureOwner(ret) is not { } declared || View(declared, formalOwner) is not { } slots ||
                CaptureOwner(BirNodeType.Of(operand as BirGraphNode)) is not { } actual ||
                View(actual, formalOwner) is not { } actualSlots)
                return null;
            bool Recovered(int index)
            {
                if (signature == null || actuals == null || actuals.Length != signature.Length) return true;
                for (int i = 0; i < signature.Length; i++)
                {
                    if (actuals[i] is BirNewArray { Elems.HasValue: true } array && array.Elems.Value.Length == 0) continue;
                    var variable = new TypeNode.Tv("method", index);
                    var ignored = inputs?.FirstOrDefault(row => row.Argument.Value == i)?.AppliedPositions.OrDefault();
                    var formal = signature[i];
                    if (ignored is { Length: > 0 } && formal is TypeNode.Fqn { Args: { } args } f)
                        formal = f with { Args = args.Where((_, p) => !ignored.Contains(p)).ToArray() };
                    bool depends = false;
                    BirGraphNameFields.VisitTypeNodes(formal, t => depends |= t == variable);
                    if (depends) return true;
                }
                return false;
            }
            return positions.Where(i => i < slots.Length && i < actualSlots.Length &&
                actualSlots[i] is (TypeNode.Star or TypeNode.Projection) && slots[i] is TypeNode.Tv { Scope: "method" } t &&
                t.I < typeArgs.Length && typeArgs[t.I] is not (TypeNode.Star or TypeNode.Projection) && !Recovered(t.I)).ToArray();
        }

        TypeNode[] View(TypeNode.Fqn type, string owner)
        {
            if (OwnerIdentity(type.Name) == owner) return type.Args;
            hierarchy ??= SupertypeGraph.CollectSource(roots);
            var seed = new SupertypeGraph.Def { Interfaces = new[] { type } };
            var views = SupertypeGraph.Reachable(seed, hierarchy, refs, sourceFrames: true)
                .Where(edge => OwnerIdentity(edge.spec.Name) == owner)
                .Select(edge => edge.spec with { Name = owner }).DistinctBy(SupertypeGraph.TypeKey).Take(2).ToArray();
            return views.Length == 1 ? views[0].Args : null;
        }

        BirGraphValue Visit(BirGraphValue node)
        {
            switch (node)
            {
                case BirCallStatic call when call.TypeArgs.OrDefault() is { Length: > 0 }
                    && !call.SuspendCall.OrDefault() && call.Args.HasValue
                    && (call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault()) is { } signature:
                    var staticCaptures = Capture(call.Args.Value, signature);
                    return staticCaptures.Length == 0 ? call : call with {
                        _capturedGenericArgument = staticCaptures,
                        _capturedGenericTarget = call.DeclarationId.HasValue ? call.DeclarationId : call._capturedGenericTarget,
                        _capturedGenericResult = call.ResultTypeStamp is { } staticResult ? staticResult : call._capturedGenericResult,
                    };
                case BirCallInstance call when call.TypeArgs.OrDefault() is { Length: > 0 }
                    && !call.SuspendCall.OrDefault() && call.Args.HasValue
                    && (call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault()) is { } signature:
                    var instanceCaptures = Capture(call.Args.Value, signature);
                    return instanceCaptures.Length == 0 ? call : call with {
                        _capturedGenericArgument = instanceCaptures,
                        _capturedGenericTarget = call.DeclarationId.HasValue ? call.DeclarationId : call._capturedGenericTarget,
                        _capturedGenericResult = call.ResultTypeStamp is { } instanceResult ? instanceResult : call._capturedGenericResult,
                    };
                default: return node;
            }
        }
    }

    internal static void SelfTestSourceFacts()
    {
        var references = ReferenceMetadataIndex.SourceCaptureTestIndex();
        var integer = new TypeNode.Fqn("kotlin.Int");
        var methodArguments = Enumerable.Repeat<TypeNode>(integer, 8).ToArray();
        TypeNode.Fqn Formal(string name, params int[] indices) => new(name,
            indices.Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray());
        BirConstant Actual(string name, int arity) => new() { Type = new TypeNode.Fqn(name,
            Enumerable.Range(0, arity).Select(_ => (TypeNode)new TypeNode.Star()).ToArray()) };
        var call = new BirCallStatic { Method = "selected", TypeArgs = methodArguments,
            Sig = new TypeNode[] { Formal("Local", 1, 2, 1), Formal("external.Frame", 4, 7) },
            Args = new IBirExpression[] { Actual("Local", 3), Actual("external.Frame", 2) },
            DeclarationId = "selected-declaration", Sty = new TypeNode.Tv("method", 3), Ret = integer };
        var parameters = new[] { new BirTypeParameter { Name = "T" },
            new BirTypeParameter { Name = "O", Variance = "out" }, new BirTypeParameter { Name = "U" } };
        var instance = new BirCallInstance { Method = "selected", TypeArgs = methodArguments,
            ShapeTypes = call.Sig, Args = call.Args, DeclarationId = call.DeclarationId, Sty = call.Sty };
        var input = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Local", Kind = "class", TypeParams = parameters },
        }, Methods = new[] { new BirFunctionDeclaration { Name = "caller", Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = instance },
            new BirExpressionStatement { Expr = call with { SuspendCall = true } },
            new BirExpressionStatement { Expr = call with { Sig = System.Array.Empty<TypeNode>(), ShapeTypes = call.Sig } },
        } } } };
        var result = PreserveSourceFacts(new[] { input }, references).Single();
        var body = result.Methods.Value[0].Body.Value;
        var captured = (BirCallStatic)((BirExpressionStatement)body[0]).Expr.Value;
        var instanceResult = (BirCallInstance)((BirExpressionStatement)body[1]).Expr.Value;
        var arguments = captured._capturedGenericArgument.Value;
        if (arguments.Length != 2 || arguments[0].Argument.Value != 0 || arguments[1].Argument.Value != 1
            || !arguments[0].OwnerPositions.Value.SequenceEqual(new[] { 0, 2 }) || !arguments[1].OwnerPositions.Value.SequenceEqual(new[] { 1 })
            || captured._capturedGenericTarget.Value != "selected-declaration"
            || captured._capturedGenericResult.Value != new TypeNode.Tv("method", 3)
            || !instanceResult._capturedGenericArgument.Value[1].OwnerPositions.Value.SequenceEqual(new[] { 1 })
            || instanceResult._capturedGenericTarget.Value != "selected-declaration"
            || ((BirCallStatic)((BirExpressionStatement)body[2]).Expr.Value)._capturedGenericArgument.IsPresent
            || ((BirCallStatic)((BirExpressionStatement)body[3]).Expr.Value)._capturedGenericArgument.IsPresent
            || call._capturedGenericArgument.IsPresent || instance._capturedGenericArgument.IsPresent)
            throw new InvalidOperationException("Typed capture facts changed argument slots, variance, declaration or generic frames");
        var localShadow = input with { Types = new IBirTypeDeclaration[] {
            input.Types.Value[0],
            new BirInterfaceDefinition { Name = "external.Frame", Kind = "interface", TypeParams = new[] {
                new BirTypeParameter { Name = "O", Variance = "out" }, new BirTypeParameter { Name = "T", Variance = "out" },
            } },
        } };
        var shadow = (BirCallStatic)((BirExpressionStatement)PreserveSourceFacts(new[] { localShadow }, references)
            .Single().Methods.Value[0].Body.Value[0]).Expr.Value;
        if (shadow._capturedGenericArgument.Value.Length != 1 || shadow._capturedGenericArgument.Value[0].Argument.Value != 0)
            throw new InvalidOperationException("Reference variance replaced an authoritative local declaration");
        var externalCall = call with { Sig = new TypeNode[] { Formal("external.Invariant", 1) },
            Args = new IBirExpression[] { Actual("external.Invariant", 1) } };
        var externalInput = input with { Methods = new[] { input.Methods.Value[0] with { Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = externalCall },
        } } } };
        BirCallStatic Imported(BirFileDeclaration file) => (BirCallStatic)((BirExpressionStatement)
            PreserveSourceFacts(new[] { file }, references).Single().Methods.Value[0].Body.Value[0]).Expr.Value;
        if (!Imported(externalInput)._capturedGenericArgument.Value.Single().OwnerPositions.Value.SequenceEqual(new[] { 0 })
            || Imported(externalInput with { Types = new IBirTypeDeclaration[] {
                new BirInterfaceDefinition { Name = "external.Invariant", Kind = "interface" },
            } })._capturedGenericArgument.IsPresent)
            throw new InvalidOperationException("Missing local type parameters fell back to a same-named reference declaration");
        bool CapturesOblivious(TypeNode slot) => Imported(externalInput with {
            Methods = new[] { input.Methods.Value[0] with { Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = externalCall with {
                    Sig = new TypeNode[] { new TypeNode.Fqn("external.Invariant", new TypeNode[] { new TypeNode.Oblivious(slot) }) },
                } },
            } } },
        })._capturedGenericArgument.IsPresent;
        var methodSlot = new TypeNode.Tv("method", 1);
        if (!CapturesOblivious(methodSlot) || CapturesOblivious(new TypeNode.Nullable(methodSlot))
            || CapturesOblivious(new TypeNode.Tv("type", 0)) || CapturesOblivious(Formal("Local", 1)))
            throw new InvalidOperationException("Oblivious capture must identify a direct declaration method slot");
        var reader = new System.Text.Json.Utf8JsonReader(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(captured.ToDom()));
        if (!reader.Read()) throw new InvalidOperationException();
        var roundtrip = BirGraphJson.ReadBirCallStatic(ref reader);
        if (reader.Read() || !JsonNode.DeepEquals(captured.ToDom(), roundtrip.ToDom()))
            throw new InvalidOperationException("Typed capture metadata lost presence or slot vectors at the codec boundary");
        Console.WriteLine("[captured source facts] self-test OK (local/reference variance, permuted frame, exact IDs/stamps, shape precedence, source isolation)");
    }

    static bool IsObject(TypeNode type) => type is TypeNode.Fqn { Name: "object" or "System.Object" } || type is TypeNode.Nullable n && IsObject(n.Of);
    static bool HasAddress(TypeNode type) => type is TypeNode.ByRef or TypeNode.Ptr || type is TypeNode.Mod m && HasAddress(m.Of);
    static bool ByRefLike(TypeNode type, ReferenceMetadataIndex refs) => type is TypeNode.Fqn f && refs.IsByRefLikeFqn(f);
    static bool ContainsCapturedSlot(TypeNode type, IReadOnlyCollection<int> slots)
    {
        var contains = false;
        Map(type, variable => {
            contains |= variable.Scope == "method" && slots.Contains(variable.I);
            return variable;
        });
        return contains;
    }
    internal static BirTypeParameter CloneParameter(BirTypeParameter parameter, Func<TypeNode, TypeNode> map)
    {
        var clone = (BirTypeParameter)parameter.DeepClone();
        if (clone.Constraints.HasValue) clone.Constraints = clone.Constraints.Value.Select(map).ToArray();
        return clone;
    }
    internal static TypeNode Map(TypeNode type, Func<TypeNode.Tv, TypeNode> tv) => type switch
    {
        TypeNode.Tv variable => tv(variable),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(arg => Map(arg, tv)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Map(n.Of, tv)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Map(o.Of, tv)),
        TypeNode.Fn f => new TypeNode.Fn(f.Suspend, Map(f.Ret, tv), f.Params.Select(type => Map(type, tv)).ToArray(),
            f.Recv == null ? null : Map(f.Recv, tv), f.Clr, f.Ctx?.Select(type => Map(type, tv)).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Map(a.Elem, tv), a.Rank, a.SzArray),
        TypeNode.ByRef r => new TypeNode.ByRef(Map(r.Of, tv)),
        TypeNode.Ptr p => new TypeNode.Ptr(Map(p.Of, tv)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, Map(m.M, tv), Map(m.Of, tv)),
        _ => type,
    };
}
