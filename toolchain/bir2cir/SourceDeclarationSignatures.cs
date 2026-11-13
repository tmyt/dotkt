using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Selected Kotlin signatures are facts, not executable DOM subtrees. Physical
// ownership binding may rename their classifiers without changing their frame.
sealed record SourceDeclarationSignature(IReadOnlyList<TypeNode> Parameters, TypeNode Return)
{
    internal JsonObject ToDom() => new() {
        ["params"] = new JsonArray(Parameters.Select(TypeJson.Write).ToArray()),
        ["ret"] = TypeJson.Write(Return),
    };

    internal SourceDeclarationSignature Rewrite(Func<TypeNode.Fqn, TypeNode.Fqn> classifier) =>
        new(Parameters.Select(type => RewriteType(type, classifier)).ToArray(), RewriteType(Return, classifier));

    internal static TypeNode RewriteType(TypeNode type, Func<TypeNode.Fqn, TypeNode.Fqn> classifier) => type switch {
        TypeNode.Fqn f => classifier(f with {
            Args = f.Args?.Select(argument => RewriteType(argument, classifier)).ToArray(),
            Classifier = f.Classifier is { } source ? source with { OwnerPath = source.OwnerPath.ToArray() } : null,
        }),
        TypeNode.Fn f => f with { Ret = RewriteType(f.Ret, classifier),
            Params = f.Params.Select(parameter => RewriteType(parameter, classifier)).ToArray(),
            Recv = f.Recv == null ? null : RewriteType(f.Recv, classifier),
            Ctx = f.Ctx?.Select(context => RewriteType(context, classifier)).ToArray() },
        TypeNode.Projection p => p with { Of = RewriteType(p.Of, classifier) },
        TypeNode.Nullable n => n with { Of = RewriteType(n.Of, classifier) },
        TypeNode.Oblivious o => o with { Of = RewriteType(o.Of, classifier) },
        TypeNode.Array a => a with { Elem = RewriteType(a.Elem, classifier) },
        TypeNode.ByRef b => b with { Of = RewriteType(b.Of, classifier) },
        TypeNode.Ptr p => p with { Of = RewriteType(p.Of, classifier) },
        TypeNode.FnPtr f => f with { Ret = RewriteType(f.Ret, classifier),
            Params = f.Params.Select(parameter => RewriteType(parameter, classifier)).ToArray() },
        TypeNode.Mod m => m with { M = RewriteType(m.M, classifier), Of = RewriteType(m.Of, classifier) },
        TypeNode.Tv or TypeNode.Star => type,
        _ => throw new InvalidOperationException("Unaccounted source signature type: " + type.GetType().Name),
    };

    internal bool SameContent(SourceDeclarationSignature other) =>
        SameTypes(Parameters, other.Parameters) && SameType(Return, other.Return);

    // Runtime type equality intentionally ignores source classifier metadata.
    // A source signature cannot use that weaker physical equality for conflicts.
    static bool SameTypes(IReadOnlyList<TypeNode> left, IReadOnlyList<TypeNode> right) =>
        left == null || right == null ? left == right
        : left.Count == right.Count && left.Select((type, index) => SameType(type, right[index])).All(equal => equal);

    static bool SameType(TypeNode left, TypeNode right) => (left, right) switch {
        (null, null) => true,
        (TypeNode.Fqn a, TypeNode.Fqn b) => a.Name == b.Name && SameTypes(a.Args, b.Args)
            && (a.Classifier == null || b.Classifier == null ? a.Classifier == b.Classifier
                : a.Classifier.PackageName == b.Classifier.PackageName && a.Classifier.Name == b.Classifier.Name
                    && a.Classifier.OwnerPath.SequenceEqual(b.Classifier.OwnerPath)),
        (TypeNode.Fn a, TypeNode.Fn b) => a.Suspend == b.Suspend && a.Clr == b.Clr
            && SameType(a.Ret, b.Ret) && SameTypes(a.Params, b.Params)
            && SameType(a.Recv, b.Recv) && SameTypes(a.Ctx ?? Array.Empty<TypeNode>(), b.Ctx ?? Array.Empty<TypeNode>()),
        (TypeNode.Tv a, TypeNode.Tv b) => a.Scope == b.Scope && a.I == b.I,
        (TypeNode.Star, TypeNode.Star) => true,
        (TypeNode.Projection a, TypeNode.Projection b) => a.Variance == b.Variance && SameType(a.Of, b.Of),
        (TypeNode.Nullable a, TypeNode.Nullable b) => SameType(a.Of, b.Of),
        (TypeNode.Oblivious a, TypeNode.Oblivious b) => SameType(a.Of, b.Of),
        (TypeNode.Array a, TypeNode.Array b) => a.Rank == b.Rank && a.SzArray == b.SzArray && SameType(a.Elem, b.Elem),
        (TypeNode.ByRef a, TypeNode.ByRef b) => SameType(a.Of, b.Of),
        (TypeNode.Ptr a, TypeNode.Ptr b) => SameType(a.Of, b.Of),
        (TypeNode.FnPtr a, TypeNode.FnPtr b) => a.CallingConvention == b.CallingConvention
            && a.GenericArity == b.GenericArity && a.RequiredParams == b.RequiredParams
            && SameType(a.Ret, b.Ret) && SameTypes(a.Params, b.Params),
        (TypeNode.Mod a, TypeNode.Mod b) => a.Req == b.Req && SameType(a.M, b.M) && SameType(a.Of, b.Of),
        _ => false,
    };
}

sealed class SourceDeclarationSignatures
{
    readonly Dictionary<string, SourceDeclarationSignature> signatures = new(StringComparer.Ordinal);

    internal bool TryGetValue(string identity, out SourceDeclarationSignature signature) =>
        signatures.TryGetValue(identity, out signature);

    internal void RewriteClassifiers(Func<TypeNode.Fqn, TypeNode.Fqn> rewrite)
    {
        foreach (var identity in signatures.Keys.ToArray())
            signatures[identity] = signatures[identity].Rewrite(rewrite);
    }

    internal BirFileDeclaration[] Capture(IReadOnlyList<BirFileDeclaration> roots)
    {
        OptionalField<BirFunctionDeclaration[]> Methods(OptionalField<BirFunctionDeclaration[]> methods) =>
            !methods.HasValue ? methods : methods.Value.Select(method => {
                if (method.DeclarationId.OrDefault() is not string id) return method;
                if (!method.Params.HasValue || !method.Ret.HasValue)
                    throw new InvalidOperationException($"declaration identity '{id}' has no complete semantic signature");
                var signature = new SourceDeclarationSignature(method.Params.Value.Select(parameter =>
                    parameter.Type.OrDefault()
                        ?? throw new InvalidOperationException($"declaration identity '{id}' has an untyped parameter"))
                    .ToArray(), method.Ret.Value).Rewrite(type => type);
                if (signatures.TryGetValue(id, out var prior) && !prior.SameContent(signature))
                    throw new InvalidOperationException($"declaration identity '{id}' has conflicting semantic signatures");
                signatures[id] = signature;
                return method with { DeclarationSourceName = method.Name };
            }).ToArray();

        // Source type declarations are flat; nested ownership is a later phase
        // fact. Do not capture functions inside bodies or inherited descriptors.
        IBirTypeDeclaration Owner(IBirTypeDeclaration type) => type switch {
            BirClassDefinition owner => owner with { Methods = Methods(owner.Methods) },
            BirInterfaceDefinition owner => owner with { Methods = Methods(owner.Methods) },
            BirEnumDefinition owner => owner with { Methods = Methods(owner.Methods) },
            _ => throw new InvalidOperationException("Unaccounted source owner: " + type.GetType().Name),
        };
        return roots.Select(root => root with {
            Methods = Methods(root.Methods),
            Types = root.Types.HasValue ? root.Types.Value.Select(Owner).ToArray() : root.Types,
        }).ToArray();
    }

    internal static void SelfTest()
    {
        var variable = new TypeNode.Tv("method", 1);
        var arguments = new TypeNode[] { variable };
        var classifier = new TypeNode.SourceClassifier("sample", new[] { "Owner" }, "Nested");
        var named = new TypeNode.Fqn("sample.Owner.Nested", arguments) { Classifier = classifier };
        TypeNode[] shapes = {
            named, variable, new TypeNode.Star(), new TypeNode.Projection("out", named),
            new TypeNode.Fn(false, named, new TypeNode[] { variable }, named, Ctx: new TypeNode[] { named }),
            new TypeNode.Nullable(named), new TypeNode.Oblivious(named), TypeNode.Array.General(named, 2),
            new TypeNode.ByRef(named), new TypeNode.Ptr(named),
            new TypeNode.FnPtr(0, 1, 1, named, new TypeNode[] { variable }),
            new TypeNode.Mod(true, named, variable),
        };
        var families = typeof(TypeNode).GetNestedTypes(System.Reflection.BindingFlags.Public)
            .Where(type => type.IsSubclassOf(typeof(TypeNode))).Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        if (!families.SetEquals(shapes.Select(type => type.GetType().Name)))
            throw new InvalidOperationException("Audit source signature traversal for the current type families");
        foreach (var shape in shapes)
        {
            var signature = new SourceDeclarationSignature(new[] { shape }, shape);
            var copy = signature.Rewrite(type => type);
            if (!signature.SameContent(copy) || !JsonNode.DeepEquals(signature.ToDom(), copy.ToDom()))
                throw new InvalidOperationException("Source signature copy changed " + shape.GetType().Name);
            var changed = signature.Rewrite(type => type with { Name = "physical+" + type.Name });
            if (shape is not (TypeNode.Tv or TypeNode.Star) && signature.SameContent(changed))
                throw new InvalidOperationException("Source signature did not bind nested classifiers");
        }

        var facts = new SourceDeclarationSignatures();
        BirFunctionDeclaration Method(string id) => new() {
            Name = "selected", DeclarationId = id,
            Params = new[] { new BirParameter { Type = named } }, Ret = variable,
        };
        var local = Method("body-local");
        var top = Method("top") with { Body = new IBirStatement[] { new BirLocalFunction { Decl = local } } };
        var original = new BirFileDeclaration {
            FileClass = "sample.File", Methods = new[] { top, new BirFunctionDeclaration { Name = "no-id" } },
            Types = new IBirTypeDeclaration[] {
                new BirClassDefinition { Methods = new[] { Method("class") } },
                new BirInterfaceDefinition { Methods = new[] { Method("interface") } },
                new BirEnumDefinition { Methods = new[] { Method("enum") } },
            },
        };
        var before = original.ToDom();
        var captured = facts.Capture(new[] { original })[0];
        if (!JsonNode.DeepEquals(before, original.ToDom()) || captured.Methods.Value[0].DeclarationSourceName.Value != "selected"
            || captured.Methods.Value[1].DeclarationSourceName.IsPresent
            || ((BirInterfaceDefinition)captured.Types.Value[1]).Methods.Value[0].DeclarationSourceName.Value != "selected"
            || facts.TryGetValue("body-local", out _))
            throw new InvalidOperationException("Source signature owner/name capture changed scope or input ownership");
        foreach (var id in new[] { "top", "class", "interface", "enum" })
            if (!facts.TryGetValue(id, out var signature) || signature.Parameters[0] != named || signature.Return != variable)
                throw new InvalidOperationException("Source signature lost a selected owner or Kotlin parameter slot");

        // Duplicate identities compare full source facts, including metadata that
        // physical TypeNode equality intentionally ignores.
        facts.Capture(new[] { original });
        var conflict = Method("top") with { Params = new[] { new BirParameter {
            Type = named with { Classifier = classifier with { PackageName = "different" } },
        } } };
        try
        {
            facts.Capture(new[] { new BirFileDeclaration { Methods = new[] { conflict } } });
            throw new Exception("Conflicting source classifier metadata was ignored");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("conflicting semantic signatures", StringComparison.Ordinal)) { }

        arguments[0] = new TypeNode.Fqn("changed");
        classifier.OwnerPath[0] = "changed";
        if (!facts.TryGetValue("top", out var snapshot) || snapshot.Parameters[0] is not TypeNode.Fqn saved
            || saved.Args[0] != variable || saved.Classifier.OwnerPath[0] != "Owner")
            throw new InvalidOperationException("Source signature snapshot aliases caller-owned arrays");
        facts.RewriteClassifiers(type => type with { Name = "bound+" + type.Name });
        if (!facts.TryGetValue("top", out var bound) || bound.Parameters[0] is not TypeNode.Fqn { Name: "bound+sample.Owner.Nested" }
            || snapshot.Parameters[0] is not TypeNode.Fqn { Name: "sample.Owner.Nested" }
            || bound.Return != variable)
            throw new InvalidOperationException("Source signature binding changed its source snapshot or generic frame");
        Console.WriteLine("[source declaration signatures] self-test OK (typed owners, source slots, classifier facts, independent binding)");
    }
}
