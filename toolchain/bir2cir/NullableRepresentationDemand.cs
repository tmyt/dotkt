using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using RepresentationRole = DotKt.Bir.NullableRepresentationFrame.Role;

// Analysis only: source declarations and bodies are not rewritten here. A body-only demand must not change an
// independently declared virtual slot. Frame materialization and metadata publication consume these facts separately.
static partial class NullableRepresentationDemand
{
    static readonly RepresentationRole[] CompanionRoles = {
        RepresentationRole.Nullable, RepresentationRole.Storage, RepresentationRole.NullableStorage,
    };

    internal sealed class Variables
    {
        readonly Dictionary<(string Scope, RepresentationRole Role), HashSet<int>> _indices = CompanionRoles
            .SelectMany(role => new[] { ("type", role), ("method", role) })
            .ToDictionary(key => key, _ => new HashSet<int>());
        public HashSet<int> Type => For("type", RepresentationRole.Nullable);
        public HashSet<int> Method => For("method", RepresentationRole.Nullable);
        public int Count => _indices.Values.Sum(indices => indices.Count);

        public HashSet<int> For(string scope, RepresentationRole role) => _indices.TryGetValue((scope, role), out var indices)
            ? indices : throw new InvalidOperationException("Unknown representation demand scope or role");

        public void Add(TypeNode.Tv variable, RepresentationRole role = RepresentationRole.Nullable) =>
            For(variable.Scope, role).Add(variable.I);
    }

    static void RequireRepresentation(TypeNode type, RepresentationRole role, Variables result)
    {
        if (type is TypeNode.Tv variable) result.Add(variable, role);
        else if (type is TypeNode.Nullable nullable)
            RequireRepresentation(nullable.Of, role == RepresentationRole.Storage ? RepresentationRole.NullableStorage : role, result);
        else if (type is TypeNode.Oblivious oblivious) RequireRepresentation(oblivious.Of, role, result);
        else if (type is TypeNode.Projection projection) RequireRepresentation(projection.Of, role, result);
    }

    static void Visit(TypeNode type, bool argument, Variables result,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, GenericRepresentationPolicy policy, bool storage = false)
    {
        switch (type)
        {
            case TypeNode.Nullable { Of: TypeNode.Tv variable } when argument:
                result.Add(variable, storage ? RepresentationRole.NullableStorage : RepresentationRole.Nullable);
                break;
            case TypeNode.Tv variable when argument && storage:
                result.Add(variable, RepresentationRole.Storage);
                break;
            case TypeNode.Nullable nullable:
                if (argument) RequireRepresentation(nullable.Of,
                    storage ? RepresentationRole.NullableStorage : RepresentationRole.Nullable, result);
                Visit(nullable.Of, false, result, frames, policy);
                break;
            case TypeNode.Oblivious oblivious:
                Visit(oblivious.Of, argument, result, frames, policy, storage);
                break;
            case TypeNode.Projection projection:
                Visit(projection.Of, argument, result, frames, policy, storage);
                break;
            case TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn or FunctionPointerVocabulary.Pointer }:
                break;
            case TypeNode.Fqn { Args: { } arguments } named:
                foreach (var item in arguments) Visit(item, true, result, frames, policy,
                    policy?.UsesStorageArguments(named.Name) == true);
                if (frames.TryGetValue(named.Name, out var frame))
                {
                    if (arguments.Length != frame.SourceArity)
                        throw new InvalidOperationException($"Constructed type '{named.Name}' has {arguments.Length} arguments, "
                            + $"but its declared nullable frame has source arity {frame.SourceArity}");
                    foreach (var slot in frame.Companions)
                        RequireRepresentation(arguments[slot.SourceIndex],
                            policy?.ApplicationRole(named.Name, slot.Representation) ?? slot.Representation, result);
                }
                break;
            case TypeNode.Array array:
                Visit(array.Elem, true, result, frames, policy);
                break;
            case TypeNode.ByRef byRef:
                Visit(byRef.Of, false, result, frames, policy);
                break;
            case TypeNode.Fn function:
                Visit(function.Ret, true, result, frames, policy);
                foreach (var parameter in function.DelegateParams) Visit(parameter, false, result, frames, policy);
                foreach (var context in function.Ctx ?? Array.Empty<TypeNode>()) Visit(context, false, result, frames, policy);
                break;
        }
    }

}
