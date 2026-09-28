using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
    // Source carriers use declaration-relative TVs; a Kotlin inner application is own-first.
    // Close those TVs through the authored declaration/application correspondence, before representation lowering.
    public JsonArray SourceDeclarationArguments(JsonNode ownerNode)
    {
        if (TypeJson.Read(ownerNode) is not TypeNode.Fqn { Args: { Length: > 0 } args } owner) return null;
        var name = SourceHierarchyName(owner.Name);
        var application = SourceHierarchyFrame(name);
        if (application == null) return new JsonArray(args.Select(TypeJson.Write).ToArray());
        if (args.Length != application.SourceArity)
            throw new InvalidOperationException($"Source carrier owner '{name}' has an inconsistent argument frame");
        _ownerNullableFrames.TryGetValue(name, out var declaration);
        return new JsonArray(Enumerable.Range(0, application.SourceArity).Select(index => {
            var physical = declaration?.SourcePosition(index) ?? index;
            var source = (TypeNode.Tv)application.SemanticVariable(new TypeNode.Tv("type", physical));
            return TypeJson.Write(args[source.I]);
        }).ToArray());
    }

    // Early owner projection consumes Kotlin applications; the late MemberRef binder still
    // uses TryReferenceTypeShape's physical graph. Frame roles and source edge carriers
    // are declaration facts, not hints to infer from generated names or argument counts.
    public bool TryReferenceSourceTypeShape(TypeNode.Fqn owner, out int typeParamCount,
        out TypeNode.Fqn baseType, out TypeNode.Fqn[] interfaces)
    {
        typeParamCount = 0;
        baseType = null;
        interfaces = Array.Empty<TypeNode.Fqn>();
        if (owner == null || IsAliasedOwner(owner.Name)) return false;
        var lookup = new TypeNode.Fqn(SourceHierarchyName(owner.Name), owner.Args);
        if (!TryReferenceTypeShapeValue(lookup, out var physical)) return false;
        var ownerFrame = SourceHierarchyFrame(lookup.Name);
        _ownerNullableFrames.TryGetValue(lookup.Name, out var declarationFrame);
        // Carriers retain declaration-relative source TVs, whereas the caller supplies
        // own-first Kotlin applications. Physical edges additionally need role restoration.
        TypeNode RestoreVariable(TypeNode.Tv variable, bool source) => ownerFrame == null ? variable
            : ownerFrame.SemanticVariable(source && declarationFrame != null
                ? new TypeNode.Tv(variable.Scope, declarationFrame.SourcePosition(variable.I)) : variable);
        typeParamCount = ownerFrame?.SourceArity ?? physical.TypeParamCount;
        TypeNode Restore(TypeNode type, bool source) => type switch
        {
            null => null,
            TypeNode.Tv { Scope: "type" } tv => RestoreVariable(tv, source),
            TypeNode.Fqn named => RestoreNamed(named, source),
            TypeNode.Nullable nullable => new TypeNode.Nullable(Restore(nullable.Of, source)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(Restore(oblivious.Of, source)),
            TypeNode.Projection projection => new TypeNode.Projection(projection.Variance, Restore(projection.Of, source)),
            TypeNode.Array array => new TypeNode.Array(Restore(array.Elem, source), array.Rank, array.SzArray),
            TypeNode.ByRef byRef => new TypeNode.ByRef(Restore(byRef.Of, source)),
            TypeNode.Ptr pointer => new TypeNode.Ptr(Restore(pointer.Of, source)),
            TypeNode.Mod modifier => new TypeNode.Mod(modifier.Req, Restore(modifier.M, source), Restore(modifier.Of, source)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Restore(fn.Ret, source),
                fn.Params.Select(arg => Restore(arg, source)).ToArray(), Restore(fn.Recv, source), fn.Clr,
                fn.Ctx?.Select(arg => Restore(arg, source)).ToArray()),
            _ => type,
        };
        TypeNode.Fqn RestoreNamed(TypeNode.Fqn named, bool source)
        {
            var args = named.Args;
            if (!source && args != null && SourceHierarchyFrame(named.Name) is { } frame)
                args = frame.OrdinaryArguments(args);
            return new TypeNode.Fqn(SourceHierarchyName(named.Name), args?.Select(arg => Restore(arg, source)).ToArray());
        }
        baseType = physical.SourceFacts?["base"] is JsonNode sourceBase
            ? Restore(TypeJson.Read(sourceBase), source: true) as TypeNode.Fqn
            : Restore(physical.Base, source: false) as TypeNode.Fqn;
        var restored = physical.Interfaces.Select(edge => (TypeNode.Fqn)Restore(edge, source: false)).ToList();
        if (physical.SourceFacts?["interfaces"] is JsonArray sourceInterfaces)
            foreach (var sourceInterface in sourceInterfaces)
            {
                var edge = (TypeNode.Fqn)Restore(TypeJson.Read(sourceInterface), source: true);
                restored.RemoveAll(candidate => candidate.Name == edge.Name);
                restored.Add(edge);
            }
        interfaces = restored.Distinct().ToArray();
        return true;
    }

    string SourceHierarchyName(string name) => _physicalTypeBySemanticName.GetValueOrDefault(name) ?? name;

    NullableRepresentationFrame SourceHierarchyFrame(string name)
    {
        name = SourceHierarchyName(name);
        if (_ownerNullableFrames.ContainsKey(name))
            return new InnerApplicationFrames(Array.Empty<JsonNode>(), this)
                .Project(_ownerNullableFrames, retainPhysicalOrder: true, selectedOwner: name)[name];
        if (!TryInnerCapturedCount(name, out var captured) || captured == 0) return null;
        if (!TryInnerSemanticOwner(name, out var outerName)
            || !TryReferenceTypeShapeValue(new TypeNode.Fqn(name), out var inner)
            || !TryReferenceTypeShapeValue(new TypeNode.Fqn(SourceHierarchyName(outerName)), out var outer))
            throw new InvalidOperationException($"Referenced inner type '{name}' has no declaring owner shape");
        // KotlinInner owns capture membership even when there are no representation companions.
        // Recurse through its declared owner to preserve multi-level [own..., outer...] ordering.
        var enclosing = SourceHierarchyFrame(outerName)
            ?? new NullableRepresentationFrame(outer.TypeParamCount, Array.Empty<int>());
        if (enclosing.SourceArity != captured)
            throw new InvalidOperationException($"Referenced inner type '{name}' has an inconsistent captured frame");
        return new NullableRepresentationFrame(inner.TypeParamCount, Array.Empty<int>())
            .WithEnclosingPrefix(enclosing, inner.TypeParamCount - captured);
    }
}
