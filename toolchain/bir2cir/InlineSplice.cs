using System.Threading;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Executable expansion owns the live typed graph until every inline call and
// prepared omission is consumed. A single identity source spans the module;
// labels, hoisted methods and capture requests belong to each file's context.
static class InlineSplice
{
    static int counter;

    public sealed class DispatchDef
    {
        public int Arity;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
    }

    internal static void Apply(BirFileDeclaration file, ReferenceMetadataIndex references,
        IReadOnlyCollection<string> moduleMethods, IReadOnlyDictionary<string, DispatchDef> dispatch,
        CompanionExtensionBinding.LocalIndex companions, bool requireCompleteDefaults)
        => new InlineExpansion(new InlinePassContext {
            File = file, References = references, Bodies = InlineBirStash.Current,
            Dispatch = dispatch, CompanionExtensions = companions,
            NextSpliceIdentity = () => Interlocked.Increment(ref counter),
            AppLocalMethods = new HashSet<string>(moduleMethods, StringComparer.Ordinal),
            NextLabelId = InlineExpansion.MaxLabelId(file) + 1,
            RequireCompleteDefaults = requireCompleteDefaults,
        }).Apply();

    internal static HashSet<string> CollectAppLocalMethodNames(IEnumerable<BirFileDeclaration> roots)
        => roots.SelectMany(root => root.Methods.OrEmpty()).Where(method => method.Name.HasValue)
            .Select(method => method.Name.Value).ToHashSet(StringComparer.Ordinal);

    internal static Dictionary<string, DispatchDef> CollectDispatchHierarchy(IEnumerable<BirFileDeclaration> roots)
    {
        var result = new Dictionary<string, DispatchDef>(StringComparer.Ordinal);
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
            if (declaration.Name.HasValue)
                result[declaration.Name.Value] = new DispatchDef {
                    Arity = declaration.TypeParams.OrEmpty().Length,
                    Base = declaration.Base.OrDefault() as TypeNode.Fqn,
                    Interfaces = declaration.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray(),
                };
        return result;
    }

    internal static (BirInlinePayload Payload, bool SameModule, string Diagnostic) SelectPayload(
        string identity, string owner, string name, int parameters, int arity, TypeNode[] signature,
        InlineBirIndex locals, ReferenceMetadataIndex references)
        => InlinePayloadSelection.Resolve<BirInlinePayload>(identity, owner, name, parameters, arity, signature,
            locals.Declaration, references == null ? null : references.InlineValueByDeclarationIdentity,
            locals.Candidates, references == null ? null : references.InlineValueCandidates,
            locals.OwnerlessCandidates, references == null ? null : references.OwnerlessInlineValueCandidates,
            payload => payload.Params.OrDefault()?.Select(parameter => parameter.Type.OrDefault()).ToArray());

    internal static (BirInlinePayload Payload, bool SameModule, string Diagnostic) ResolvePayload(
        BirCallInline call, InlineBirIndex locals, ReferenceMetadataIndex references)
    {
        var callee = (call.Callee.OrDefault() as TypeNode.Fqn)?.Name;
        var name = callee?.Contains('.') == true ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
        var selected = SelectPayload(call.DeclarationId.OrDefault(), (call.Owner.OrDefault() as TypeNode.Fqn)?.Name,
            name, call.Pc.OrDefault(), call.Ga.OrDefault(), call.ParamSig.OrDefault(), locals, references);
        return (selected.Payload == null ? null : (BirInlinePayload)selected.Payload.DeepClone(),
            selected.SameModule, selected.Diagnostic);
    }
}
