using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // Delegation names the selected OPEN constructor signature, independently of
    // the derived class's use-site type arguments. Preserve that declaration in
    // the reference; close only the delegate slots its actual arguments fill.
    internal static void ResolveBaseConstructors(BirFileDeclaration root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypes, bool metadataBuild = false)
    {
        foreach (var type in root.Types.OrEmpty())
        {
            if (type.Base.OrDefault() is not TypeNode.Fqn baseFqn || localTypes.Contains(baseFqn.Name)) continue;
            var open = ResolveOwnerType(baseFqn, refs);
            if (open == null) continue;
            foreach (var ctor in type.Ctors.OrEmpty())
            {
                if (ctor.ThisArgs.HasValue || !ctor.BaseArgs.HasValue) continue;
                var arguments = ctor.BaseArgs.Value;
                var signature = ctor.DelegationSig.OrEmpty().Where(slot => slot != null).ToList();
                if (signature.Count != arguments.Length)
                    throw new InvalidOperationException($"bir2cir: constructor delegation to '{baseFqn.Name}' carries "
                        + $"{arguments.Length} arguments but {signature.Count} signature slots");
                var candidates = open.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Length == signature.Count).ToList();
                var winner = ctor.DelegationDeclarationId.HasValue
                    ? refs.SelectConstructorDeclaration(candidates, ctor.DelegationDeclarationId.Value)
                    : ctor.DelegationNativeKey.HasValue ? refs.SelectedNativeConstructor(open, ctor.DelegationNativeKey.Value)
                    : ClrDeclarationMatching.PickDeclaredConstructor(refs, candidates, signature,
                        $"base constructor owner={TypeNode.ToJson(baseFqn)} ({DescArgs(signature)})",
                        metadataBuild ? InterfaceSlotTypeComparer.Instance : null);
                var reference = metadataBuild ? ClrMemberReferences.MetadataDeclaration(refs, winner, baseFqn.Args ?? [])
                    : MemberRefOf(localTypes, refs, winner, MemberRefNode.Kinds.Ctor, open, baseFqn.Args);
                ctor.BaseCtorRef = BirMemberReference.FromPhysical(reference);
                // BaseCtorRef owns the physical slots. Keep the selected Kotlin
                // signature for conversions such as a function object to a delegate.
                var parameters = winner.GetParameters();
                for (var index = 0; index < parameters.Length; index++)
                {
                    var slot = SupertypeGraph.SubstOwnerTvs(metadataBuild ? reference.ParameterTypes[index]
                        : ClrDeclarationMatching.RefTypeOf(refs, localTypes, parameters[index].ParameterType), baseFqn.Args ?? []);
                    MarkDelegateSlot((BirGraphValue)arguments[index], SubstituteMethodTypeArgs(slot, []), refs, localTypes);
                }
                ctor.DelegationDeclarationId = default;
                ctor.DelegationNativeKey = default;
            }
        }
    }
}
