using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    internal sealed class SyntheticTypesSnapshot
    {
        internal readonly SyntheticTypeRecipe[] Types;
        internal readonly InnerApplicationOrder.DeclarationFact[] InnerOrder;
        internal SyntheticTypesSnapshot(SyntheticTypeRecipe[] types, InnerApplicationOrder.DeclarationFact[] innerOrder)
        { Types = types; InnerOrder = innerOrder; }
    }

    internal sealed record SyntheticTypeRecipe(string Name, MethodSnapshot[] Methods,
        TypeNode[][] Constructors, KeyValuePair<string, TypeNode>[] Slots);

    // Capture the exact pre-adaptation declaration clock, without touching the
    // continuing index or retaining any mutable declaration. Admission happens
    // at the original per-file point, not eagerly for all module files.
    internal static SyntheticTypesSnapshot CaptureNewSyntheticTypes(BirFileDeclaration file, DeclIndex index)
        => new(file.Types.OrEmpty().Where(type => type.Name.HasValue && !index.ByOwner.ContainsKey(type.Name.Value))
                .Select(CaptureRecipe).ToArray(),
            InnerApplicationOrder.Capture(file));

    internal static void ApplyNewSyntheticTypes(SyntheticTypesSnapshot snapshot, DeclIndex index)
    {
        index.AdvanceSourceAdmission();
        index.SourceApplicationOrder?.Add(snapshot.InnerOrder);
        foreach (var recipe in snapshot.Types) AdmitNewType(index, recipe.Name, () => CloneRecipe(recipe));
    }

    static SyntheticTypeRecipe CaptureRecipe(IBirTypeDeclaration type)
    {
        var slots = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        CollectSlots(type.Fields.OrEmpty(), type.Properties.OrEmpty(), slots);
        return new(type.Name.Value,
            type.Methods.OrEmpty().Select(method => ReadMethodSnapshot(method)).Where(method => method != null).ToArray(),
            type.Ctors.OrEmpty().Select(ctor => ReadParams(ctor.Params.OrEmpty(), preferCarrier: false))
                .Where(parameters => parameters != null).ToArray(), slots.ToArray());
    }

    static SyntheticTypeRecipe CaptureRecipe(JsonObject type)
    {
        var slots = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        CollectSlots(type["fields"], slots);
        CollectSlots(type["properties"], slots);
        return new(Str(type["name"]),
            (type["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(ReadMethodSnapshot).Where(method => method != null).ToArray(),
            (type["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(ctor => ReadParams(ctor["params"])).Where(parameters => parameters != null).ToArray(),
            slots.ToArray());
    }

    static SyntheticTypeRecipe CloneRecipe(SyntheticTypeRecipe recipe)
    {
        var methods = recipe.Methods.Select(method => {
            var sig = method.Signature;
            var copy = new DeclSig {
                Ret = BirGraphValue.CopyType(sig.Ret), Params = sig.Params.Select(BirGraphValue.CopyType).ToArray(),
                NullableErasureOwnershipKnown = sig.NullableErasureOwnershipKnown,
                ParamsRefused = sig.ParamsRefused?.ToArray(),
            };
            return method with { Signature = copy };
        }).ToArray();
        return new(recipe.Name, methods,
            recipe.Constructors.Select(parameters => parameters.Select(BirGraphValue.CopyType).ToArray()).ToArray(),
            recipe.Slots.Select(slot => new KeyValuePair<string, TypeNode>(slot.Key, BirGraphValue.CopyType(slot.Value))).ToArray());
    }

    // Both current owning readers use this add-only transition. The factory
    // supplies independently owned facts: fresh captures need no second deep
    // copy, and saved recipes clone only when a new owner is actually admitted.
    static void AdmitNewType(DeclIndex index, string owner, Func<SyntheticTypeRecipe> read)
    {
        if (index.ByOwner.ContainsKey(owner)) return;
        var recipe = read();
        index.RecordOwnerAdmission(owner);
        var signatures = new Dictionary<string, DeclSig>(StringComparer.Ordinal);
        var properties = new Dictionary<string, List<DeclSig>>(StringComparer.Ordinal);
        foreach (var method in recipe.Methods) AddMethodSnapshot(index, owner, method, signatures, properties);
        index.ByOwner[owner] = signatures;
        index.PropertiesByOwner[owner] = properties;
        var constructors = new Dictionary<int, TypeNode[]>();
        foreach (var parameters in recipe.Constructors)
            AddConstructorSnapshot(constructors, parameters);
        index.Ctors[owner] = constructors;
        var slots = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var (name, type) in recipe.Slots) AddSlotSnapshot(slots, name, type);
        index.Slots[owner] = slots;
    }
}
