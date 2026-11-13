using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using System.Text.Json.Nodes;

static partial class FaithfulHints
{
    // Owning producers transfer each operand once. The shared specification owns
    // helper selection/signatures, independently of either graph representation.
    internal static BirCallStatic CollToString(IBirExpression operand, CollKind kind, TypeNode[] arguments)
    {
        var specification = CollectionRenderer(kind, arguments);
        var operands = new List<IBirExpression> { operand };
        if (specification.MapPreference)
            operands.Add(MemberCallSubstitution.StaticMapPreferenceValue(new TypeNode.Fqn("kotlin.collections.Map", arguments), null,
                MemberCallSubstitution.CollectionOperationPhase.Source));
        return Helper(specification, operands.ToArray());
    }

    internal static BirCallStatic LibraryToString(IBirExpression operand) => Helper(NullableRenderer(), new[] { operand });
    internal static BirCallStatic ElemToString(IBirExpression operand) => Helper(ElementRenderer(), new[] { operand });

    static BirCallStatic Helper(HelperSpecification specification, IBirExpression[] arguments)
    {
        var call = new BirCallStatic {
            Owner = new TypeNode.Fqn(specification.Owner), Method = specification.Method,
            Sig = specification.Signature, Args = arguments,
            TypeArgs = specification.TypeArguments == null ? default : OptionalField<TypeNode[]>.Of(specification.TypeArguments),
        };
        return _bindHelper?.Invoke(call) ?? call;
    }
}

static partial class FaithfulHintRecognition
{
    enum RenderSite { Object, Print, Concat }
    enum Renderer { None, Collection, Element, Nullable }
    readonly record struct Rendering(Renderer Kind, FaithfulHints.CollKind Collection = default, TypeNode[] Arguments = null);

    static Rendering SelectRendering(RenderSite site, TypeNode surface, TypeNode value)
    {
        if (site == RenderSite.Print && FaithfulHints.IsStarProjectedColl(surface)) return new(Renderer.Element);
        if (FaithfulHints.ClassifyColl(value) is { } collection)
            return new(Renderer.Collection, collection.kind, collection.args);
        return site == RenderSite.Concat && FaithfulHints.IsNullable(value) ? new(Renderer.Nullable) : default;
    }

    static JsonNode Render(JsonNode operand, Rendering rendering) => rendering.Kind switch {
        Renderer.Collection => FaithfulHints.CollToString(FaithfulHints.StripAnyCast(operand), rendering.Collection, rendering.Arguments),
        Renderer.Element => FaithfulHints.ElemToString(operand),
        Renderer.Nullable => FaithfulHints.LibraryToString(operand),
        _ => operand,
    };
    static IBirExpression Render(IBirExpression operand, Rendering rendering) => rendering.Kind switch {
        Renderer.Collection => FaithfulHints.CollToString(FaithfulHints.StripAnyCast(operand), rendering.Collection, rendering.Arguments),
        Renderer.Element => FaithfulHints.ElemToString(operand),
        Renderer.Nullable => FaithfulHints.LibraryToString(operand),
        _ => operand,
    };

    static string ReinterpretElement(string method, TypeNode receiver, IReadOnlySet<string> localTopLevelFns)
    {
        var (source, element) = method switch {
            "toByteArray" => ("kotlin.UByteArray", "kotlin.Byte"),
            "toUByteArray" => ("kotlin.ByteArray", "kotlin.UByte"),
            _ => ((string)null, null),
        };
        return source != null && !localTopLevelFns.Contains(method) && receiver is TypeNode.Fqn owner && owner.Name == source
            ? element : null;
    }

    static string CompareHelper(string method, TypeNode owner) => method == "compareTo" && owner is TypeNode.Fqn named
        ? ReferenceMetadataIndex.BareOwnerFqn(named.Name) switch {
            "kotlin.Double" => "clrDoubleCompare", "kotlin.Float" => "clrFloatCompare", _ => null,
        } : null;

    internal static BirGraphValue Apply(BirGraphValue root, IReadOnlySet<string> localTopLevelFns)
        => SourceGraphRewrite.Rewrite(root, (node, scope) => node switch {
            BirObjectMethod value => Transform(value, scope),
            BirCallStatic value => Transform(value, scope, localTopLevelFns),
            BirConcat value => Transform(value, scope),
            BirCallInstance value when CompareHelper(value.Method.OrDefault(), value.OwnerType.OrDefault()) is { } method
                && value.Recv.HasValue && value.Args.HasValue && value.Args.Value.Length == 1 =>
                FaithfulHints.FloatCall(method, value.Recv.Value, value.Args.Value[0]),
            _ => node,
        });

    static BirGraphValue Transform(BirObjectMethod call, IReadOnlyDictionary<string, TypeNode> scope)
    {
        if (call.Method.OrDefault() != "ToString" || !call.Recv.HasValue) return call;
        var rendering = SelectRendering(RenderSite.Object, null, BirGraphStaticType.Value(call.Recv.Value, scope));
        return rendering.Kind == Renderer.None ? call : (BirGraphValue)Render(call.Recv.Value, rendering);
    }

    static BirGraphValue Transform(BirCallStatic call, IReadOnlyDictionary<string, TypeNode> scope, IReadOnlySet<string> localTopLevelFns)
    {
        if (call.Owner.OrDefault() == null && call.OwnerType.OrDefault() == null && call.Args.HasValue && call.Args.Value.Length == 1
            && ReinterpretElement(call.Method.OrDefault(), BirGraphStaticType.Surface(call.Args.Value[0], scope), localTopLevelFns) is { } element)
            return new BirCast { Type = new TypeNode.Array(new TypeNode.Fqn(element)), E = OptionalField<IBirExpression>.Of(call.Args.Value[0]) };
        if (call.Owner.OrDefault() != null || call.Method.OrDefault() is not ("println" or "print") || !call.Args.HasValue) return call;
        for (var index = 0; index < call.Args.Value.Length; index++)
        {
            var argument = call.Args.Value[index];
            call.Args.Value[index] = Render(argument,
                SelectRendering(RenderSite.Print, BirGraphStaticType.Surface(argument, scope), BirGraphStaticType.Value(argument, scope)));
        }
        return call;
    }

    static BirGraphValue Transform(BirConcat concat, IReadOnlyDictionary<string, TypeNode> scope)
    {
        if (concat.Parts.HasValue)
            for (var index = 0; index < concat.Parts.Value.Length; index++)
            {
                var part = concat.Parts.Value[index];
                concat.Parts.Value[index] = Render(part, SelectRendering(RenderSite.Concat, null, BirGraphStaticType.Value(part, scope)));
            }
        return concat;
    }
}
