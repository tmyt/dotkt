using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class AliasHelperHoist
{
    internal static BirFileDeclaration Apply(BirFileDeclaration root, ReferenceMetadataIndex refs,
        GenericRepresentationPolicy representations, AliasDeclarationTransitions transitions,
        Action<BirFunctionDeclaration> retireActual)
    {
        var original = root.Types.OrEmpty();
        RehomeGeneratedMethods(root, original, refs);
        var aliases = original.Where(type => IsAliasTypeDef(type.Name.OrDefault(), refs, out _))
            .Select(type => type.Name.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var type in original)
            if (type.Vis.OrDefault() == "private"
                && (aliases.Contains(type.NestedIn.OrDefault() ?? "") || aliases.Contains(type.SemanticOwner.OrDefault() ?? "")))
                type.Vis = "internal";
        var continuing = new List<IBirTypeDeclaration>();
        var replacements = new List<AliasDeclarationTransitions.Replacement>();
        foreach (var type in original)
        {
            if (!IsAliasTypeDef(type.Name.OrDefault(), refs, out var owner)) { continuing.Add(type); continue; }
            var host = BuildOwnershipHost(type, original);
            var helper = BuildHelper(type, owner, refs, representations);
            foreach (var method in type.Methods.OrEmpty()) retireActual(method);
            replacements.Add(new(type, host, helper));
            if (host != null) continuing.Add(host);
            if (helper != null) continuing.Add(helper);
        }
        var output = replacements.Count == 0 ? original : continuing.ToArray();
        if (replacements.Count != 0) root.Types = output;
        transitions.Record(root, original, output, replacements);
        return root;
    }

    static bool IsAliasTypeDef(string name, ReferenceMetadataIndex refs, out string fqn)
    {
        fqn = null;
        if (name == null) return false;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(name);
        if (!refs.Aliases.ContainsKey(bare) && !IsNativeArrayOwner(bare)) return false;
        fqn = bare;
        return true;
    }

    static BirClassDefinition BuildOwnershipHost(IBirTypeDeclaration owner, IBirTypeDeclaration[] types)
    {
        if (!owner.Name.HasValue) return null;
        var children = types.Where(type => type.NestedIn.OrDefault() == owner.Name.Value
            || type.SemanticOwner.OrDefault() == owner.Name.Value).ToArray();
        if (children.Length == 0) return null;
        var companions = children.Where(type => type.CompanionCarrier.HasValue)
            .Where(type => type.Name.HasValue).Select(type => type.Name.Value).ToHashSet(StringComparer.Ordinal);
        return new BirClassDefinition {
            Name = owner.Name, Kind = "class", Generated = true, Abstract = false,
            Vis = owner.Vis.HasValue ? owner.Vis : OptionalField<string>.Of("public"),
            Base = OptionalField<TypeNode>.Null, Interfaces = Array.Empty<TypeNode>(),
            Fields = owner.Fields.OrEmpty().Where(field => field.Static.OrDefault()
                && field.Type.OrDefault() is TypeNode.Fqn target && companions.Contains(target.Name)).ToArray(),
            Ctors = Array.Empty<BirConstructor>(), Methods = Array.Empty<BirFunctionDeclaration>(),
            Properties = Array.Empty<BirPropertyDeclaration>(), TypeParams = owner.TypeParams,
            CapturedTypeParams = owner.CapturedTypeParams, NestedIn = owner.NestedIn, SemanticOwner = owner.SemanticOwner,
        };
    }

    static BirClassDefinition BuildHelper(IBirTypeDeclaration declaration, string owner,
        ReferenceMetadataIndex refs, GenericRepresentationPolicy representations)
    {
        if (declaration.Kind.OrDefault() != "class") return null;
        var frame = NullableRepresentationViews.TypeFacts((BirGraphValue)declaration)?.NullableFrame
            ?? new NullableRepresentationFrame(declaration.TypeParams.OrEmpty().Length, Array.Empty<int>());
        var receiver = ReceiverType(declaration.Name.Value, frame, representations);
        var inlineValue = declaration.Mods.OrDefault()?.Value.OrDefault() == true;
        var methods = new List<BirFunctionDeclaration>();
        foreach (var method in declaration.Methods.OrEmpty())
        {
            if (!method.Name.HasValue || method.Static.OrDefault() || !method.Body.HasValue) continue;
            if (KotlinPropertyAccessors.TryIdentity(method, out _, out _)
                && method.Body.Value.Any(statement => BodyReadsBackingField((BirGraphValue)statement))) continue;
            var objectSlot = method.ObjectOverride.OrDefault();
            if (UsesNativeArrayObjectSlot(owner, objectSlot) || inlineValue && objectSlot) continue;
            if (refs.IsRule3Member(owner, method.Name.Value))
                methods.Add(HoistMethod(method, declaration.TypeParams.OrDefault(), receiver));
        }
        if (methods.Count == 0) return null;
        return new BirClassDefinition { Name = ReferenceMetadataIndex.HelperTypeName(owner), Kind = "class",
            Generated = true, Abstract = false, Vis = "public", Base = OptionalField<TypeNode>.Null,
            Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<BirFieldDeclaration>(),
            Ctors = Array.Empty<BirConstructor>(), Methods = methods.ToArray() };
    }

    static BirFunctionDeclaration HoistMethod(BirFunctionDeclaration source, BirTypeParameter[] ownerParameters,
        TypeNode receiver)
    {
        var ownerArity = ownerParameters?.Length ?? 0;
        TypeNode Rebind(TypeNode type) => RewriteType(type, variable => variable.Scope switch {
            "type" => new TypeNode.Tv("method", variable.I),
            "method" => new TypeNode.Tv("method", ownerArity + variable.I), _ => variable,
        });
        var method = (BirFunctionDeclaration)source.DeepClone();
        RewriteLexicalTypes(method, Rebind);
        var parameters = ownerParameters?.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        if (parameters != null) foreach (var parameter in parameters) RewriteLexicalTypes(parameter, Rebind);
        var all = (parameters ?? Array.Empty<BirTypeParameter>()).Concat(method.TypeParams.OrEmpty()).ToArray();
        var result = new BirFunctionDeclaration { Name = method.Name, Static = true, Override = false,
            Virtual = false, Abstract = false, ObjectOverride = false, Vis = "public",
            Params = new[] { new BirParameter { Name = "__self", Type = Rebind(receiver) } }
                .Concat(method.Params.OrEmpty()).ToArray(),
            Ret = method.Ret.IsPresent ? method.Ret : OptionalField<TypeNode>.Null,
            Body = method.Body };
        if (all.Length != 0) result.TypeParams = all;
        foreach (var statement in result.Body.OrEmpty()) RewriteThis((BirGraphValue)statement);
        if (source.DeclarationId.HasValue)
        {
            result.DeclarationId = DeclarationIdentity(source.DeclarationId.Value);
            result.DeclarationSourceName = source.DeclarationSourceName.HasValue ? source.DeclarationSourceName
                : throw new InvalidOperationException($"Hoisted declaration '{source.DeclarationId.Value}' has no preserved source name");
            result.Generated = true;
        }
        return result;
    }

    // Descriptor ownership comes from the maintained model. In particular new's
    // argTypes are lexical, whereas memberSignature and ordinary call sig are not.
    static void RewriteLexicalTypes(BirGraphValue node, Func<TypeNode, TypeNode> rewrite)
    {
        if (node is IBirExecutablePayload or BirMemberReference) return;
        static bool Moves(BirGenericFrameRole role) => role is not (BirGenericFrameRole.ReferencedDeclaration
            or BirGenericFrameRole.ResolvedDeclaration or BirGenericFrameRole.OverrideDeclaration
            or BirGenericFrameRole.FrozenSourceFact or BirGenericFrameRole.StorageSourceType);
        node.RewriteGenericFrameInPlace((role, type) => Moves(role) ? rewrite(type) : type,
            (role, child) => { if (Moves(role)) RewriteLexicalTypes(child, rewrite); });
    }

    static bool BodyReadsBackingField(BirGraphValue root)
    {
        if (root is IBirExecutablePayload) return false;
        var found = root is BirFieldGet or BirFieldSetExpression or BirStaticFieldGet or BirStaticFieldSet;
        root.VisitChildren(child => { if (!found) found = BodyReadsBackingField(child); });
        return found;
    }

    static void RewriteThis(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirNewSuspendLambda suspend)
        {
            if (suspend.CapValues.HasValue)
                for (var index = 0; index < suspend.CapValues.Value.Length; index++)
                {
                    if (suspend.CapValues.Value[index] is not BirGraphValue value) continue;
                    if (value is BirThis) suspend.CapValues.Value[index] = new BirLocal { Name = "__self" };
                    else RewriteThis(value);
                }
            return;
        }
        node.VisitChildSlots(slot => { if (slot.Value is BirThis) slot.Replace(new BirLocal { Name = "__self" }); else RewriteThis(slot.Value); });
    }

    static void RehomeGeneratedMethods(BirFileDeclaration root, IBirTypeDeclaration[] types, ReferenceMetadataIndex refs)
    {
        if (!root.Methods.HasValue || !root.FileClass.HasValue) return;
        var aliases = types.Where(type => IsAliasTypeDef(type.Name.OrDefault(), refs, out _))
            .Select(type => type.Name.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var method in root.Methods.Value)
        {
            if (!method.Name.HasValue || !method.SemanticOwner.HasValue || !aliases.Contains(method.SemanticOwner.Value)) continue;
            var owner = method.SemanticOwner.Value;
            var frame = GeneratedMethodFrame(root, owner, method.Name.Value, method.TypeParams.OrEmpty().Length);
            RewriteLexicalTypes(method, type => RemapTypeVariables(type, frame));
            method.SemanticOwner = root.FileClass;
            VisitLive(method, node => {
                if (node is BirNewClosure closure && closure.SynthClass.OrDefault() is { } ingredients
                    && ingredients.SemanticOwner.OrDefault() == owner) ingredients.SemanticOwner = root.FileClass;
                if (node is BirNewSam sam && sam.SynthClass.OrDefault() is { } implementation
                    && implementation.SemanticOwner.OrDefault() == owner) implementation.SemanticOwner = root.FileClass;
            });
            VisitLive(root, node => {
                switch (node)
                {
                    case BirNewDelegate edge when edge.Method.OrDefault() == method.Name.Value
                        && edge.CalleeOwner.OrDefault() is TypeNode.Fqn target && target.Name == owner:
                        edge.CalleeOwner = new TypeNode.Fqn(root.FileClass.Value); break;
                    case BirCallStatic edge when edge.Method.OrDefault() == method.Name.Value
                        && edge.CalleeOwner.OrDefault() is TypeNode.Fqn target && target.Name == owner:
                        edge.CalleeOwner = new TypeNode.Fqn(root.FileClass.Value); break;
                    case BirNewBoundDelegate edge when edge.Method.OrDefault() == method.Name.Value
                        && edge.CalleeOwner.OrDefault() is TypeNode.Fqn target && target.Name == owner:
                        edge.CalleeOwner = new TypeNode.Fqn(root.FileClass.Value); break;
                }
            });
        }
    }

    static Dictionary<(string Scope, int Index), int> GeneratedMethodFrame(BirFileDeclaration root,
        string owner, string name, int arity)
    {
        if (arity == 0) return new();
        Dictionary<(string Scope, int Index), int> result = null;
        VisitLive(root, node => {
            if (node is not BirNewDelegate edge || edge.Method.OrDefault() != name
                || edge.CalleeOwner.OrDefault() is not TypeNode.Fqn target || target.Name != owner
                || !edge.TypeArgs.HasValue || edge.TypeArgs.Value.Length != arity) return;
            var candidate = new Dictionary<(string Scope, int Index), int>();
            for (var index = 0; index < arity; index++)
            {
                if (edge.TypeArgs.Value[index] is not TypeNode.Tv variable) return;
                candidate[(variable.Scope, variable.I)] = index;
            }
            if (candidate.Count != arity) throw new InvalidOperationException(
                $"generated alias helper '{owner}.{name}' does not carry a one-to-one lexical generic frame");
            if (result != null && !result.OrderBy(pair => pair.Key).SequenceEqual(candidate.OrderBy(pair => pair.Key)))
                throw new InvalidOperationException($"generated alias helper '{owner}.{name}' carries inconsistent lexical generic frames");
            result = candidate;
        });
        return result ?? throw new InvalidOperationException($"generated alias helper '{owner}.{name}' has no newDelegate edge defining its generic frame");
    }

    static void VisitLive(BirGraphValue root, Action<BirGraphValue> visit)
    {
        if (root is IBirExecutablePayload) return;
        visit(root);
        root.VisitChildren(child => VisitLive(child, visit));
    }
}
