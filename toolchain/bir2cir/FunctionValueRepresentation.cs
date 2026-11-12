using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Ordinary Kotlin function variance must not change the identity of the stored value.
// Choose argument/result carriers at construction, rather than allocating adapters at
// each assignment or generic use. A function carrying an explicit physical delegate
// family retains its exact signature.
static class FunctionValueRepresentation
{
    internal const string RestorationKey = "functionValueRestoration";
    static readonly TypeNode Object = new TypeNode.Fqn("object");

    // Preserve the complete source slot, including nested generic function arguments,
    // before either nullable-frame expansion or function representation changes it.
    public static BirFileDeclaration[] PreserveSourceFacts(IReadOnlyList<BirFileDeclaration> roots)
    {
        static OptionalField<T[]> Map<T>(OptionalField<T[]> source, Func<T, T> rewrite) =>
            source.HasValue ? source.Value.Select(rewrite).ToArray() : source;
        static OptionalField<TypeNode> Slot(OptionalField<TypeNode> type, OptionalField<TypeNode> previous) =>
            !previous.HasValue && ContainsOrdinaryFunction(type.OrDefault()) ? type : previous;
        BirParameter Parameter(BirParameter parameter) => parameter with { KotlinType = Slot(parameter.Type, parameter.KotlinType) };
        BirFunctionDeclaration Method(BirFunctionDeclaration method) => method with {
            RetKotlinType = Slot(method.Ret, method.RetKotlinType), Params = Map(method.Params, Parameter),
        };
        BirConstructor Constructor(BirConstructor constructor) => constructor with { Params = Map(constructor.Params, Parameter) };
        BirFieldDeclaration Field(BirFieldDeclaration field) => field with { KotlinType = Slot(field.Type, field.KotlinType) };
        BirPropertyDeclaration Property(BirPropertyDeclaration property) => property with { KotlinType = Slot(property.Type, property.KotlinType) };

        OptionalField<SourceTypeFacts> Edges(OptionalField<TypeNode> baseType, OptionalField<TypeNode[]> interfaces,
            OptionalField<BirTypeParameter[]> parameters, OptionalField<SourceTypeFacts> previous)
        {
            var original = previous.OrDefault() ?? SourceTypeFacts.Empty;
            var bounds = parameters.OrEmpty().Select((parameter, index) => (parameter, index))
                .Where(entry => entry.parameter.Constraints.OrEmpty().Any(ContainsOrdinaryFunction))
                .ToDictionary(entry => entry.index, entry => entry.parameter.Constraints.Value);
            var merged = original.Merge(ContainsOrdinaryFunction(baseType.OrDefault()) ? baseType.Value : null,
                interfaces.OrEmpty().Where(ContainsOrdinaryFunction).ToArray(), bounds);
            return ReferenceEquals(original, merged) ? previous : merged;
        }
        IBirTypeDeclaration Owner(IBirTypeDeclaration declaration) => declaration switch {
            BirClassDefinition owner => owner with {
                KotlinSupertypesPre = Edges(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
                Methods = Map(owner.Methods, Method), Ctors = Map(owner.Ctors, Constructor),
                Fields = Map(owner.Fields, Field), Properties = Map(owner.Properties, Property),
            },
            BirInterfaceDefinition owner => owner with {
                KotlinSupertypesPre = Edges(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
                Methods = Map(owner.Methods, Method), Ctors = Map(owner.Ctors, Constructor),
                Fields = Map(owner.Fields, Field), Properties = Map(owner.Properties, Property),
            },
            BirEnumDefinition owner => owner with {
                KotlinSupertypesPre = Edges(owner.Base, owner.Interfaces, owner.TypeParams, owner.KotlinSupertypesPre),
                Methods = Map(owner.Methods, Method), Ctors = Map(owner.Ctors, Constructor),
                Fields = Map(owner.Fields, Field), Properties = Map(owner.Properties, Property),
            },
            _ => throw new InvalidOperationException("Unaccounted function-source owner: " + declaration.GetType().Name),
        };
        return roots.Select(root => root with {
            Methods = Map(root.Methods, Method), Fields = Map(root.Fields, Field),
            Properties = Map(root.Properties, Property), Types = Map(root.Types, Owner),
        }).ToArray();
    }

    internal static bool ContainsOrdinaryFunction(TypeNode type) => type switch {
        TypeNode.Fqn { Name: FunctionPointerVocabulary.Pointer } => false,
        TypeNode.Fqn f => f.Args?.Any(ContainsOrdinaryFunction) == true,
        TypeNode.Fn { Suspend: false, Clr: null } => true,
        TypeNode.Fn f => ContainsOrdinaryFunction(f.Ret) || f.Params.Any(ContainsOrdinaryFunction)
            || ContainsOrdinaryFunction(f.Recv) || f.Ctx?.Any(ContainsOrdinaryFunction) == true,
        TypeNode.Array a => ContainsOrdinaryFunction(a.Elem),
        TypeNode.Nullable n => ContainsOrdinaryFunction(n.Of),
        TypeNode.Oblivious o => ContainsOrdinaryFunction(o.Of),
        TypeNode.Projection p => ContainsOrdinaryFunction(p.Of),
        TypeNode.ByRef b => ContainsOrdinaryFunction(b.Of),
        TypeNode.Ptr p => ContainsOrdinaryFunction(p.Of),
        TypeNode.FnPtr f => ContainsOrdinaryFunction(f.Ret) || f.Params.Any(ContainsOrdinaryFunction),
        TypeNode.Mod m => ContainsOrdinaryFunction(m.M) || ContainsOrdinaryFunction(m.Of),
        _ => false,
    };

    internal static bool ContainsOrdinaryFunction(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (IsNativeSignature(obj)) return false;
            if (obj["t"] is JsonValue tag && tag.TryGetValue<string>(out var kind) && kind == "fn"
                && TypeJson.Read(obj) is TypeNode.Fn { Suspend: false, Clr: null }) return true;
            return obj.Any(pair => pair.Value != null && ContainsOrdinaryFunction(pair.Value));
        }
        return node is JsonArray array && array.Any(ContainsOrdinaryFunction);
    }

    public static void Apply(JsonNode root, ValueTypeOracle isByRefLike)
    {
        FunctionSignatureIdentity.Capture(root);
        Walk(root, isByRefLike);
    }

    static TypeNode Carrier(TypeNode type, ValueTypeOracle isByRefLike) => type switch
    {
        // Changing the CLR value representation must not change the slot's nullability contract.
        TypeNode.Nullable n => new TypeNode.Nullable(Carrier(n.Of, isByRefLike)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Carrier(o.Of, isByRefLike)),
        TypeNode.Fqn named when isByRefLike(named) => type,
        _ => Object,
    };

    static void Walk(JsonNode node, ValueTypeOracle isByRefLike)
    {
        if (node is JsonObject obj)
        {
            // A FunctionN nested here describes a native callable signature,
            // not a stored Kotlin function value. Its slots cannot be erased.
            if (IsNativeSignature(obj)) return;
            if (obj["k"] is JsonValue callTag && callTag.TryGetValue<string>(out var callKind)
                && callKind is "callStatic" or "callInstance" or "constrainedCall"
                && obj[FunctionSignatureIdentity.CallKey] == null
                && obj["sig"] is JsonArray signature && ContainsOrdinaryFunction(signature))
                obj[FunctionSignatureIdentity.CallKey] = signature.DeepClone();
            var invocationResult = obj["k"] is JsonValue nodeTag
                && nodeTag.TryGetValue<string>(out var nodeKind) && nodeKind == "delegateInvoke"
                && TypeJson.Read(obj["funcType"]) is TypeNode.Fn { Suspend: false, Clr: null } invocation
                    ? invocation.Ret : null;
            if (obj["t"] is JsonValue tag && tag.TryGetValue<string>(out var kind) && kind == "fn")
            {
                var fn = (TypeNode.Fn)TypeJson.Read(obj);
                if (fn.Suspend || fn.Clr != null) return;
                // A ref struct cannot cross an object slot. Keep its exact CLR signature even for a stored
                // Kotlin function; all boxable slots still share the ordinary identity-preserving carrier.
                var physical = new TypeNode.Fn(false, Carrier(fn.Ret, isByRefLike),
                    fn.Params.Select(parameter => Carrier(parameter, isByRefLike)).ToArray(),
                    fn.Recv == null ? null : Carrier(fn.Recv, isByRefLike));
                var replacement = (JsonObject)TypeJson.Write(physical);
                obj.Clear();
                foreach (var pair in replacement.ToList()) obj[pair.Key] = pair.Value?.DeepClone();
                return;
            }
            foreach (var pair in obj.ToList())
            {
                // These are authored Kotlin facts, not physical slots.
                if (pair.Key is DeclarationIdentityBinding.SemanticSignatureKey or FunctionSignatureIdentity.Key or FunctionSignatureIdentity.CallKey
                    or "memberSignature" or "delegationSig" or "inheritedClassMethods" or "attrs" or "retAttrs") continue;
                // A literal SAM conversion constructs its declared CLR delegate directly. Its target
                // signature is not an ordinary Kotlin function value, and can contain unboxable slots.
                // Stored function conversions still erase their operand and adapt at the SAM boundary.
                if (pair.Key == "funcType" && ClrMemberResolution.HasDeclaredDelegateSlot(obj)) continue;
                if (pair.Value != null) Walk(pair.Value, isByRefLike);
            }
            if (invocationResult != null)
            {
                var call = obj.DeepClone();
                var resultType = TypeJson.Write(invocationResult);
                Walk(resultType, isByRefLike);
                obj.Clear();
                obj["k"] = "cast";
                obj["type"] = resultType;
                // This restores the function's selected result slot. It is not a source-level
                // unchecked classifier cast and must retain its constructed generic arguments.
                obj["_exactBridgeCast"] = true;
                obj[RestorationKey] = true;
                obj["e"] = call;
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child != null) Walk(child, isByRefLike);
    }

    static bool IsNativeSignature(JsonObject type) => type["t"] is JsonValue tag
        && tag.TryGetValue<string>(out var kind) && kind == "fqn"
        && type["name"] is JsonValue name && name.TryGetValue<string>(out var fqn)
        && fqn == FunctionPointerVocabulary.Pointer;

    internal static void SelfTestSourceFacts()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var function = new TypeNode.Fn(false, new TypeNode.Nullable(new TypeNode.Tv("method", 1)),
            new TypeNode[] { new TypeNode.Tv("type", 0) }, new TypeNode.Fqn("Receiver"),
            Ctx: new TypeNode[] { new TypeNode.Fqn("Context") });
        var native = new TypeNode.Fqn(FunctionPointerVocabulary.Pointer, new TypeNode[] { function });
        var samples = new (TypeNode Type, bool ContainsFunction)[] {
            (function, true), (native, false), (new TypeNode.Fqn("Box", new TypeNode[] { function }), true),
            (integer, false), (new TypeNode.Tv("type", 0), false), (new TypeNode.Star(), false),
            (new TypeNode.Projection("in", function), true), (new TypeNode.Nullable(function), true),
            (new TypeNode.Oblivious(function), true), (new TypeNode.Array(function), true),
            (new TypeNode.ByRef(function), true), (new TypeNode.Ptr(function), true),
            (new TypeNode.FnPtr(0, 0, 0, function, Array.Empty<TypeNode>()), true),
            (new TypeNode.Mod(true, function, integer), true),
            (new TypeNode.Fn(true, integer, Array.Empty<TypeNode>()), false),
            (new TypeNode.Fn(true, function, Array.Empty<TypeNode>()), true),
            (new TypeNode.Fn(true, integer, Array.Empty<TypeNode>(), Recv: function), true),
            (new TypeNode.Fn(true, integer, Array.Empty<TypeNode>(), Ctx: new TypeNode[] { function }), true),
            (new TypeNode.Fn(false, integer, Array.Empty<TypeNode>(), Clr: "System.Func"), false),
            (new TypeNode.Fn(false, function, Array.Empty<TypeNode>(), Clr: "System.Func"), true),
        };
        var families = typeof(TypeNode).GetNestedTypes(System.Reflection.BindingFlags.Public)
            .Where(type => type.IsSubclassOf(typeof(TypeNode))).Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        if (!families.SetEquals(samples.Select(sample => sample.Type.GetType().Name)))
            throw new InvalidOperationException("Audit ordinary function recognition for the current type families");
        foreach (var (type, containsFunction) in samples)
            if (ContainsOrdinaryFunction(type) != containsFunction || ContainsOrdinaryFunction(TypeJson.Write(type)) != containsFunction)
                throw new InvalidOperationException("Typed function recognition changed " + type.GetType().Name);

        var baseType = new TypeNode.Fqn("Base", new TypeNode[] { function });
        var edge = new TypeNode.Fqn("Contract", new TypeNode[] { function });
        var sibling = new TypeNode.Fqn("Sibling");
        BirParameter Parameter(TypeNode type) => new() { Name = "value", Type = type };
        var method = new BirFunctionDeclaration { Name = "value", Ret = function,
            Params = new[] { Parameter(function), Parameter(native), Parameter(integer) } };
        var field = new BirFieldDeclaration { Name = "value", Type = function };
        var property = new BirPropertyDeclaration { Name = "value", Type = function };
        var root = new BirFileDeclaration { FileClass = "Functions", Methods = new[] { method },
            Fields = new[] { field }, Properties = new[] { property },
            Types = new IBirTypeDeclaration[] {
                new BirClassDefinition { Name = "Owner", Kind = "class", Base = baseType,
                    Interfaces = new[] { edge }, Methods = new[] { method }, Fields = new[] { field },
                    Properties = new[] { property }, Ctors = new[] { new BirConstructor { Params = new[] { Parameter(function) } } },
                    TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] {
                        new TypeNode.Fqn("kotlin.Comparable"), edge, sibling,
                    } } },
                },
                new BirInterfaceDefinition { Name = "ContractOwner", Kind = "interface", Interfaces = new[] { edge },
                    Methods = new[] { method }, Fields = new[] { field }, Properties = new[] { property } },
                new BirEnumDefinition { Name = "EnumOwner", Kind = "enum", Methods = new[] { method }, Interfaces = new[] { edge } },
            },
        };
        root = ComparableRepresentationLowering.Apply(new[] { root }, referenceBuild: false)[0];
        var before = root.ToDom();
        var rewritten = PreserveSourceFacts(new[] { root })[0];
        var result = rewritten.Methods.Value[0];
        var owner = (BirClassDefinition)rewritten.Types.Value[0];
        if (!JsonNode.DeepEquals(before, root.ToDom()) || result.RetKotlinType.Value != function
            || result.Params.Value[0].KotlinType.Value != function || result.Params.Value[1].KotlinType.IsPresent
            || result.Params.Value[2].KotlinType.IsPresent || rewritten.Fields.Value[0].KotlinType.Value != function
            || rewritten.Properties.Value[0].KotlinType.Value != function
            || owner.Ctors.Value[0].Params.Value[0].KotlinType.Value != function
            || owner.Methods.Value[0].RetKotlinType.Value != function || owner.Fields.Value[0].KotlinType.Value != function
            || owner.Properties.Value[0].KotlinType.Value != function
            || ((BirInterfaceDefinition)rewritten.Types.Value[1]).Methods.Value[0].RetKotlinType.Value != function
            || ((BirInterfaceDefinition)rewritten.Types.Value[1]).KotlinSupertypesPre.Value.Interfaces.Single() != edge
            || ((BirEnumDefinition)rewritten.Types.Value[2]).KotlinSupertypesPre.Value.Interfaces.Single() != edge
            || ((BirEnumDefinition)rewritten.Types.Value[2]).Methods.Value[0].RetKotlinType.Value != function)
            throw new InvalidOperationException("Function source facts lost slots, ownership or native signature boundaries");
        var expectedOwner = root.Types.Value[0] as BirClassDefinition;
        var expected = expectedOwner.ToDom();
        KotlinSupertypesRecord.Merge(expected, new JsonObject {
            ["base"] = TypeJson.Write(baseType), ["interfaces"] = new JsonArray(TypeJson.Write(edge)),
            ["bounds"] = new JsonObject { ["0"] = new JsonArray(TypeJson.Write(edge), TypeJson.Write(sibling)) },
        });
        if (SourceTypeFacts.ToDom(owner.KotlinSupertypesPre.Value).GetValue<string>()
                != expected[KotlinSupertypesRecord.PreKey].GetValue<string>()
            || owner.KotlinSupertypesPre.Value.Bounds[0][0] is not TypeNode.Fqn { Name: "kotlin.Comparable" })
            throw new InvalidOperationException("Function source facts lost earliest sibling bounds or exact leaf order");
        Console.WriteLine("[function source facts] self-test OK (typed slots, all type families, native signatures, least-erased edges/bounds)");
    }
}
