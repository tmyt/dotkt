using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ClrEventImplBinding
{
    internal static void SelfTestLocalEventRecipes() => LocalEvents.SelfTestRecipes();

    public sealed partial class LocalEvents
    {
        internal static void SelfTestRecipes()
        {
            var handler = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Unit"), Array.Empty<TypeNode>());
            BirClassDefinition Owner(string ownerName) => new() { Name = ownerName, Kind = "class",
                ClrEvents = new[] { new BirClrEventBacking { K = "clrEventBacking", Name = "Changed", HandlerType = handler, Modality = "open" } },
                Methods = new[] {
                    new BirFunctionDeclaration { Name = "add_Changed", Params = new[] { new BirParameter { Name = "value", Type = handler, KotlinType = handler } },
                        Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirThis() } } },
                    new BirFunctionDeclaration { Name = "add_Changed", Params = new[] { new BirParameter { Name = "value", Type = handler, KotlinType = handler } },
                        Body = new IBirStatement[] { new BirClrEventAccessor { Kind = "add", Event = "Changed" } } },
                } };
            var owner = Owner("Base");
            var add = owner.Methods.Value[1];
            var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
            var events = new LocalEvents();
            events.CaptureSource(file);
            if (!events.declarations[("Base", "Changed")].SourceOwned)
                throw new InvalidOperationException("Event recipe was projected before the declaration handoff");
            bool refused = false;
            try { events.TryGet(new TypeNode.Fqn("Base"), "Changed", out _); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Event recipe consumption lazily crossed an unbound frontier");
            owner.Methods = owner.Methods.Value.Take(1).ToArray(); // exact accessor is detached, as after stripping
            add.Params.Value[0].Type = new TypeNode.Fqn("ChangedAfterCapture");
            owner.ClrEvents.Value[0].Modality = "final";
            events.BindDomRecipes();
            if (!events.TryGet(new TypeNode.Fqn("Base"), "Changed", out var selected)
                || events.declarations[("Base", "Changed")].SourceOwned || selected.Add.Parent != null
                || TypeJson.Read(selected.Add["params"][0]["type"]) is not TypeNode.Fn
                || Str(selected.Backing["modality"]) != "open")
                throw new InvalidOperationException("Pre-strip event recipe lost its independent accessor or retained typed staging");
            events.Index(new BirFileDeclaration { Types = new IBirTypeDeclaration[] { Owner("Base"), Owner("Late") } }.ToDom());
            if (!events.TryGet(new TypeNode.Fqn("Base"), "Changed", out var same)
                || !ReferenceEquals(same.Add, selected.Add) || !ReferenceEquals(same.Backing, selected.Backing)
                || !events.TryGet(new TypeNode.Fqn("Late"), "Changed", out _))
                throw new InvalidOperationException("Late event indexing replaced an authoritative recipe or lost a new owner");
            refused = false;
            try { events.CaptureSource(file); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Event source recipes were recaptured after handoff");
            Console.WriteLine("[local event recipes] self-test OK (exact semantic accessor, pre-strip clone, one-way handoff, first writer and late additions)");
        }
    }
}
