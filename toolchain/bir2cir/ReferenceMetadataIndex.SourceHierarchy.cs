using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
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
        var ownerFrame = _ownerNullableFrames.GetValueOrDefault(lookup.Name);
        typeParamCount = ownerFrame?.SourceArity ?? physical.TypeParamCount;
        TypeNode Restore(TypeNode type, bool source) => type switch
        {
            null => null,
            TypeNode.Tv { Scope: "type" } tv when !source && ownerFrame != null => ownerFrame.SemanticVariable(tv),
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
            if (!source && args != null && _ownerNullableFrames.TryGetValue(named.Name, out var frame))
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
}
