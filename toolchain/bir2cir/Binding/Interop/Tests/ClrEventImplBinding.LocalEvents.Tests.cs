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
            if (!events.TryGetSource(new TypeNode.Fqn("Base"), "Changed", out var captured)
                || ReferenceEquals(captured.Add, add) || ReferenceEquals(captured.Backing, owner.ClrEvents.Value[0]))
                throw new InvalidOperationException("Event source recipes do not own independent pre-strip snapshots");
            owner.Methods = owner.Methods.Value.Take(1).ToArray(); // exact accessor is detached, as after stripping
            add.Params.Value[0].Type = new TypeNode.Fqn("ChangedAfterCapture");
            owner.ClrEvents.Value[0].Modality = "final";
            events.IndexSource(new BirFileDeclaration { Types = new IBirTypeDeclaration[] { Owner("Base"), Owner("Late") } });
            if (!events.TryGetSource(new TypeNode.Fqn("Base"), "Changed", out var selected)
                || !ReferenceEquals(selected.Add, captured.Add) || !ReferenceEquals(selected.Backing, captured.Backing)
                || selected.Add.Params.Value[0].Type.Value is not TypeNode.Fn
                || selected.Backing.Modality.Value != "open"
                || !events.TryGetSource(new TypeNode.Fqn("Late"), "Changed", out _))
                throw new InvalidOperationException("Typed event indexing replaced a captured recipe or lost a synthesized owner");
            events.ReleaseSourceRecipes();
            if (!events.released || events.declarations.Count != 0)
                throw new InvalidOperationException("Finished event preparation retained source staging");
            bool refused = false;
            try { events.CaptureSource(file); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Finished event preparation admitted another source recipe");
            refused = false;
            try { events.TryGetSource(new TypeNode.Fqn("Base"), "Changed", out _); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Finished event preparation exposed detached source recipes");
            Console.WriteLine("[local event recipes] self-test OK (exact semantic accessor, pre-strip clone, first writer, late typed additions and explicit release)");
        }
    }
}
