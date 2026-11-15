using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

static partial class UnsafeAccessorLowering
{
    // Family permission follows declaration ancestry, not equality of constructed generic arguments. The selected
    // member's complete owner/signature remains untouched; this only decides whether CLR access is already legal.
    // Declaration/CLR-access policy. Receiver typing is supplied by the phase's
    // existing physical expression oracle, not reconstructed from source names.
    class FamilyAccessPolicy<THost> where THost : class
    {
        readonly IReadOnlyDictionary<string, THost> _hosts;
        readonly IReadOnlyDictionary<string, SupertypeGraph.Def> _types;
        readonly ReferenceMetadataIndex _refs;
        readonly Func<THost, string> _name, _nested, _visibility;
        readonly Func<THost, TypeNode.Fqn> _self;
        readonly Func<string, string, bool> _private;

        internal FamilyAccessPolicy(IReadOnlyDictionary<string, THost> hosts,
            IReadOnlyDictionary<string, SupertypeGraph.Def> types, ReferenceMetadataIndex refs,
            Func<THost, string> name, Func<THost, string> nested, Func<THost, string> visibility,
            Func<THost, TypeNode.Fqn> self, Func<string, string, bool> privateAccess)
        { _hosts = hosts; _types = types; _refs = refs; _name = name; _nested = nested;
            _visibility = visibility; _self = self; _private = privateAccess; }

        internal bool Allows(THost caller, TypeNode receiverType, TypeNode.Fqn owner, string visibility,
            bool isStatic, bool hasReceiver, IEnumerable<TypeNode> signature)
        {
            if (visibility != "protected" || isStatic || !hasReceiver) return false;
            if (Head(receiverType) is not TypeNode.Fqn instance || !Visible(caller, owner)
                || signature.Any(type => type == null || !Visible(caller, type))) return false;
            // A closure can retain the family rights of its enclosing derived class, but its receiver must still
            // be that derived class (or a subclass), not an arbitrary instance of the protected declaring base.
            foreach (var context in Contexts(caller))
                if (_self(context) is TypeNode.Fqn family
                    && Derives(family, owner) && Derives(instance, family)) return true;
            return false;
        }

        bool Derives(TypeNode.Fqn source, TypeNode.Fqn target) =>
            SupertypeGraph.ReachesClassDeclaration(source, target, _types, _refs);

        IEnumerable<THost> Contexts(THost caller)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (caller != null && seen.Add(_name(caller)))
            {
                yield return caller;
                caller = _nested(caller) is string nested && _hosts.TryGetValue(nested, out var parent) ? parent : null;
            }
        }

        bool Visible(THost caller, TypeNode type)
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
                        return VisibleReferenced(caller, _refs?.ResolveRefType(name, owner.Args?.Length ?? 0));
                    }
                    if (_nested(host) == null) return true; // Every local top-level type is assembly-accessible.
                    if (!Visible(caller, new TypeNode.Fqn(_nested(host)))) return false;
                    return _visibility(host) switch
                    {
                        "private" => _private(_name(caller), _nested(host)),
                        "protected" => Contexts(caller).Any(context =>
                            Derives(_self(context), new TypeNode.Fqn(_nested(host)))),
                        _ => true,
                    };
                default: return false;
            }
        }

        bool VisibleReferenced(THost caller, Type declaration)
        {
            if (declaration == null) return false;
            if (declaration.IsVisible) return true;
            if (!declaration.IsNested || !VisibleReferenced(caller, declaration.DeclaringType)) return false;
            if (declaration.IsNestedPublic) return true;
            if (!declaration.IsNestedFamily && !declaration.IsNestedFamORAssem) return false;
            // This is the exact referenced TypeDef identity, including its declared generic arity, not a new
            // operand construction. Another assembly's internal/private type cannot gain family permission.
            var enclosing = new TypeNode.Fqn(declaration.DeclaringType.FullName);
            return Contexts(caller).Any(context =>
                Derives(_self(context), enclosing));
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
