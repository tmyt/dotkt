using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ClrEventImplBinding
{
    // Canonical recipes are captured before MemberStrip can remove an accessor.
    // Early binding and later source synthesis share this sole first-writer authority.
    public sealed partial class LocalEvents
    {
        sealed class EventRecipe
        {
            internal readonly BirClrEventBacking Backing;
            internal readonly BirFunctionDeclaration Add;
            internal EventRecipe(BirClrEventBacking backing, BirFunctionDeclaration add)
            {
                Backing = (BirClrEventBacking)backing.DeepClone();
                Add = (BirFunctionDeclaration)add.DeepClone();
            }
        }

        readonly Dictionary<(string Owner, string Event), EventRecipe> declarations = new();
        bool released;

        public void CaptureSource(BirFileDeclaration file)
        {
            RequireSource();
            foreach (var owner in file.Types.OrEmpty())
            {
                if (!owner.Name.HasValue) continue;
                foreach (var backing in owner.ClrEvents.OrEmpty())
                {
                    if (backing.K.OrDefault() != "clrEventBacking" || !backing.Name.HasValue) continue;
                    var name = backing.Name.Value;
                    var key = (owner.Name.Value, name);
                    if (declarations.ContainsKey(key)) continue;
                    var add = FindAccessor(owner.Methods.OrEmpty(), "add_" + name, "add");
                    if (add != null) declarations.Add(key, new EventRecipe(backing, add));
                }
            }
        }

        // Synthesis may introduce a new declaration after the pre-strip capture.
        // Existing source recipes must never be reconstructed from the mutated owner.
        internal void IndexSource(BirFileDeclaration file) => CaptureSource(file);

        internal bool TryGetSource(TypeNode.Fqn owner, string name,
            out (BirClrEventBacking Backing, BirFunctionDeclaration Add) declaration)
        {
            RequireSource();
            if (declarations.TryGetValue((owner.Name, name), out var recipe))
            { declaration = (recipe.Backing, recipe.Add); return true; }
            declaration = default; return false;
        }

        public void ReleaseSourceRecipes()
        {
            RequireSource();
            declarations.Clear();
            released = true;
        }

        void RequireSource()
        {
            if (released) throw new InvalidOperationException("Event source recipes already finished their source lifetime");
        }
    }
}
