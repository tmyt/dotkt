using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Owning adapter for the source checkpoint. StampAgrees remains the one
// conservative relation; no expression-type inference belongs in this walk.
static partial class SourceStampSanity
{
    internal static void Check(BirFileDeclaration file)
    {
        CheckContainer(file.FileClass.OrDefault() ?? "?", file.Methods.OrEmpty(),
            Array.Empty<BirConstructor>(), file.Fields.OrEmpty(), file.Pos.OrDefault(), false);
        // Match the checkpoint's declaration domain, not arbitrary declaration
        // metadata or encoded executable/source carriers.
        foreach (var type in file.Types.OrEmpty())
            CheckContainer(type.Name.OrDefault() ?? "?", type.Methods.OrEmpty(),
                type.Ctors.OrEmpty(), type.Fields.OrEmpty(), type switch {
                    BirClassDefinition value => value.Pos.OrDefault(),
                    BirInterfaceDefinition value => value.Pos.OrDefault(),
                    BirEnumDefinition value => value.Pos.OrDefault(),
                    _ => throw new InvalidOperationException("Unknown owning type declaration"),
                }, type.Kind.OrDefault() == "interface");
    }

    static void CheckContainer(string owner, BirFunctionDeclaration[] methods, BirConstructor[] constructors,
        BirFieldDeclaration[] fields, BirDeclarationPosition position, bool isInterface)
    {
        foreach (var method in methods)
            if (!method.Abstract.OrDefault() && method.Body.HasValue)
                CheckForest(Prefix(method.Pos.OrDefault()) + owner + "." + (method.Name.OrDefault() ?? "?"), method.Body.Value.Cast<BirGraphValue>());
        foreach (var constructor in constructors)
        {
            if (!constructor.Body.HasValue) continue;
            var label = Prefix(constructor.Pos.OrDefault()) + owner + "..ctor";
            CheckForest(label, constructor.Body.Value.Cast<BirGraphValue>());
            CheckForest(label, constructor.PreStmts.OrEmpty().Cast<BirGraphValue>());
            CheckForest(label, constructor.ThisArgs.OrEmpty().Cast<BirGraphValue>());
            CheckForest(label, constructor.BaseArgs.OrEmpty().Cast<BirGraphValue>());
        }
        if (!isInterface)
            CheckForest(Prefix(position) + owner + "..cctor", fields.Where(field => field.Static.OrDefault() && field.Init.HasValue)
                .Select(field => (BirGraphValue)field.Init.Value));
    }

    static string Prefix(BirDeclarationPosition position)
    {
        if (position?.F.OrDefault() is not string file) return "";
        var name = Path.GetFileName(file);
        return position.L.HasValue && position.L.Value >= 0 ? name + ":" + position.L.Value + ": " : name + ": ";
    }

    static void CheckForest(string declaration, IEnumerable<BirGraphValue> roots)
    {
        void Walk(BirGraphValue value)
        {
            if (value == null || value is IBirExecutablePayload) return;
            if (value is BirGraphNode node)
            {
                TypeNode stamp = null, result = null, dynamicResult = null;
                node.VisitOwnTypeFields((field, type) => {
                    if (field == "sty") stamp = type;
                    else if (field == "ret") result = type;
                    else if (field == "dynRet") dynamicResult = type;
                });
                if (stamp != null)
                {
                    Compare("ret", result);
                    Compare("dynRet", dynamicResult);
                }
                void Compare(string field, TypeNode type)
                {
                    if (type == null || IrSanity.StampAgrees(stamp, type)) return;
                    throw new IrSanityException(declaration,
                        $"'{node.Tag}' carries a stale 'sty': the stamp names {TypeNode.ToJson(stamp)} while its '{field}' names "
                        + $"{TypeNode.ToJson(type)} — a pass that changes a node's result type must rewrite or delete its 'sty'");
                }
            }
            value.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
        }
        foreach (var root in roots) Walk(root);
    }
}
