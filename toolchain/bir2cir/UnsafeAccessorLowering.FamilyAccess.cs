using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class UnsafeAccessorLowering
{
    // Family permission follows declaration ancestry, not equality of constructed generic arguments. The selected
    // member's complete owner/signature remains untouched; this only decides whether CLR access is already legal.
    sealed class FamilyAccess
    {
        readonly IReadOnlyDictionary<string, Host> _hosts;
        readonly IReadOnlyDictionary<string, SupertypeGraph.Def> _types;
        readonly ReferenceMetadataIndex _refs;
        readonly Func<JsonNode, BirScope, TypeNode> _physicalType;

        internal FamilyAccess(IReadOnlyList<JsonNode> roots, IReadOnlyDictionary<string, Host> hosts,
            ReferenceMetadataIndex refs)
        {
            _hosts = hosts;
            _types = SupertypeGraph.Collect(roots);
            _refs = refs;
            _physicalType = PhysicalValueCoercion.ResultTypeOracle(roots);
        }

        internal bool Allows(Host caller, JsonNode receiver, TypeNode.Fqn owner, string visibility,
            bool isStatic, BirScope scope, IEnumerable<TypeNode> signature)
        {
            if (visibility != "protected" || isStatic || receiver == null) return false;
            var receiverType = receiver is JsonObject expression && Str(expression["k"]) == "this"
                ? TypeJson.Read(SelfType(caller)) : _physicalType(receiver, scope);
            if (Head(receiverType) is not TypeNode.Fqn instance || !Visible(caller, owner)
                || signature.Any(type => type == null || !Visible(caller, type))) return false;
            // A closure can retain the family rights of its enclosing derived class, but its receiver must still
            // be that derived class (or a subclass), not an arbitrary instance of the protected declaring base.
            foreach (var context in Contexts(caller))
                if (TypeJson.Read(SelfType(context)) is TypeNode.Fqn family
                    && Derives(family, owner) && Derives(instance, family)) return true;
            return false;
        }

        bool Derives(TypeNode.Fqn source, TypeNode.Fqn target) =>
            SupertypeGraph.ReachesClassDeclaration(source, target, _types, _refs);

        IEnumerable<Host> Contexts(Host caller)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (caller != null && seen.Add(caller.Name))
            {
                yield return caller;
                caller = caller.NestedIn != null && _hosts.TryGetValue(caller.NestedIn, out var parent) ? parent : null;
            }
        }

        bool Visible(Host caller, TypeNode type)
        {
            switch (type)
            {
                case TypeNode.Tv: return true;
                case TypeNode.Nullable nullable: return Visible(caller, nullable.Of);
                case TypeNode.Oblivious oblivious: return Visible(caller, oblivious.Of);
                case TypeNode.ByRef reference: return Visible(caller, reference.Of);
                case TypeNode.Array array: return Visible(caller, array.Elem);
                case TypeNode.Mod modifier: return Visible(caller, modifier.M) && Visible(caller, modifier.Of);
                case TypeNode.Fn { Suspend: false } function:
                    // Function slots already use the shared delegate representation contract. Visibility follows
                    // that physical construction; the selected signature itself must not be rewritten here.
                    var physical = function.Clr != null ? function : BirTypeLowering.PhysicalDelegate(
                        BirTypeLowering.DelegateReturnSlot(function), function.DelegateParams);
                    return Visible(caller, BirTypeLowering.DelegateFqnOf(physical));
                case TypeNode.Fqn { Name: "void" or "System.Void", Args: null }: return true;
                case TypeNode.Fqn owner:
                    if (owner.Args != null && owner.Args.Any(argument => !Visible(caller, argument))) return false;
                    if (!_hosts.TryGetValue(owner.Name, out var host))
                    {
                        // Unresolved/nonpublic external types do not grant a new direct-access proof. Keep the
                        // existing accessor path; exact referenced metadata, not a source name, establishes visibility.
                        var name = _refs?.TryResolveClrOwner(owner.Name, out var alias, out _) == true ? alias : owner.Name;
                        name = ((TypeNode.Fqn)BirTypeLowering.CanonicalPhysicalSlotType(new TypeNode.Fqn(name))).Name;
                        return _refs?.ResolveRefType(name, owner.Args?.Length ?? 0)?.IsVisible == true;
                    }
                    if (host.NestedIn == null) return true; // Every local top-level type is assembly-accessible.
                    if (!Visible(caller, new TypeNode.Fqn(host.NestedIn))) return false;
                    return Str(host.Declaration["vis"]) switch
                    {
                        "private" => DirectPrivateAccessIsValid(caller.Name, host.NestedIn, _hosts),
                        "protected" => Contexts(caller).Any(context =>
                            Derives((TypeNode.Fqn)TypeJson.Read(SelfType(context)), new TypeNode.Fqn(host.NestedIn))),
                        _ => true,
                    };
                default: return false;
            }
        }

        static TypeNode Head(TypeNode type) => type switch
        {
            TypeNode.Nullable nullable => Head(nullable.Of),
            TypeNode.Oblivious oblivious => Head(oblivious.Of),
            TypeNode.ByRef reference => Head(reference.Of),
            _ => type,
        };
    }
}
