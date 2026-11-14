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

    public static BirFileDeclaration[] PreserveSourceFacts(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
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
        return roots.Select(root => (BirFileDeclaration)root.Rewrite(Visit)).ToArray();

        CapturedGenericArgument[] Capture(IBirExpression[] arguments, TypeNode[] signature)
        {
            var captures = new List<CapturedGenericArgument>();
            for (var index = 0; index < arguments.Length && index < signature.Length; index++)
            {
                if (signature[index] is not TypeNode.Fqn { Args: { } formalArgs } formal
                    || BirNodeType.Of(arguments[index] as BirGraphNode) is not TypeNode.Fqn { Args: { } actualArgs } actual
                    || formal.Name != actual.Name || formalArgs.Length != actualArgs.Length) continue;
                var local = locals.TryGetValue(formal.Name, out var localParameters);
                var parameters = local ? localParameters : refs.OwnerTypeParametersInPhysicalFrame(formal.Name);
                var frame = !local && refs.NullableTypeFrames.TryGetValue(formal.Name, out var referencedFrame)
                    ? referencedFrame : null;
                if (parameters == null || (frame?.SourceArity ?? parameters.Count) != formalArgs.Length) continue;
                var slots = formalArgs.Select((type, slot) => type is TypeNode.Tv { Scope: "method" }
                        && actualArgs[slot] is TypeNode.Star
                        && parameters[frame?.SourcePosition(slot) ?? slot].Variance.OrDefault() is not ("in" or "out")
                        ? ((TypeNode.Tv)type).I : -1)
                    .Where(slot => slot >= 0).Distinct().ToArray();
                if (slots.Length != 0) captures.Add(new CapturedGenericArgument { Argument = index, Slots = slots });
            }
            return captures.ToArray();
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
            || !arguments[0].Slots.Value.SequenceEqual(new[] { 1 }) || !arguments[1].Slots.Value.SequenceEqual(new[] { 7 })
            || captured._capturedGenericTarget.Value != "selected-declaration"
            || captured._capturedGenericResult.Value != new TypeNode.Tv("method", 3)
            || !instanceResult._capturedGenericArgument.Value[1].Slots.Value.SequenceEqual(new[] { 7 })
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
        if (!Imported(externalInput)._capturedGenericArgument.Value.Single().Slots.Value.SequenceEqual(new[] { 1 })
            || Imported(externalInput with { Types = new IBirTypeDeclaration[] {
                new BirInterfaceDefinition { Name = "external.Invariant", Kind = "interface" },
            } })._capturedGenericArgument.IsPresent)
            throw new InvalidOperationException("Missing local type parameters fell back to a same-named reference declaration");
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
    static bool ContainsCapturedSlot(TypeNode type, IReadOnlyList<int> slots)
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
