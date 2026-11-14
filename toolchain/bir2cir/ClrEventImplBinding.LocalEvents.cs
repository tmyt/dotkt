using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEventImplBinding
{
    // Canonical recipes are captured before MemberStrip can remove an accessor.
    // One authority crosses source -> DOM once; later synthesis extends it under
    // the same first-writer rule, rather than reconstructing source recipes.
    public sealed partial class LocalEvents
    {
        sealed class EventRecipe
        {
            BirClrEventBacking sourceBacking;
            BirFunctionDeclaration sourceAdd;
            internal JsonObject Backing;
            internal JsonObject Add;
            internal bool SourceOwned => sourceBacking != null || sourceAdd != null;

            internal EventRecipe(BirClrEventBacking backing, BirFunctionDeclaration add)
            { sourceBacking = (BirClrEventBacking)backing.DeepClone(); sourceAdd = (BirFunctionDeclaration)add.DeepClone(); }
            internal EventRecipe(JsonObject backing, JsonObject add)
            { Backing = (JsonObject)backing.DeepClone(); Add = (JsonObject)add.DeepClone(); }
            internal void BindDom()
            {
                if (sourceBacking == null || sourceAdd == null || Backing != null || Add != null)
                    throw new InvalidOperationException("Event source recipe already crossed its owning frontier");
                Backing = sourceBacking.ToDom(); Add = sourceAdd.ToDom();
                sourceBacking = null; sourceAdd = null;
            }
        }

        readonly Dictionary<(string Owner, string Event), EventRecipe> declarations = new();
        bool bound;

        public void CaptureSource(BirFileDeclaration file)
        {
            if (bound) throw new InvalidOperationException("Event recipes already crossed their source frontier");
            foreach (var owner in file.Types.OrEmpty())
            {
                if (!owner.Name.HasValue) continue;
                foreach (var backing in owner.ClrEvents.OrEmpty())
                {
                    if (backing.K.OrDefault() != "clrEventBacking" || !backing.Name.HasValue) continue;
                    var name = backing.Name.Value;
                    var key = (owner.Name.Value, name);
                    if (declarations.ContainsKey(key)) continue;
                    var add = owner.Methods.OrEmpty().FirstOrDefault(method => method.Body.HasValue
                        && method.Body.Value.FirstOrDefault() is BirClrEventAccessor marker
                        && IsEventAccessor(method.Name.OrDefault(), "add_" + name, method.Body.Value.Length,
                            marker.Tag, marker.Kind.OrDefault(), "add"));
                    if (add != null) declarations.Add(key, new EventRecipe(backing, add));
                }
            }
        }

        public void BindDomRecipes()
        {
            if (bound) throw new InvalidOperationException("Event recipes already crossed their source frontier");
            foreach (var recipe in declarations.Values) recipe.BindDom();
            bound = true;
        }

        void RequireBound()
        {
            if (!bound) throw new InvalidOperationException("Event recipes were consumed before their DOM handoff");
        }

        public void Index(JsonNode node)
        {
            RequireBound();
            if (node is JsonObject obj)
            {
                if (Str(obj["name"]) is string owner && obj["clrEvents"] is JsonArray events
                    && obj["methods"] is JsonArray methods)
                    foreach (var backing in events.OfType<JsonObject>())
                        if (Str(backing["k"]) == "clrEventBacking" && Str(backing["name"]) is string name
                            && !declarations.ContainsKey((owner, name))
                            && FindAccessor(methods, "add_" + name, "add") is JsonObject add)
                            declarations.Add((owner, name), new EventRecipe(backing, add));
                if (obj["types"] is JsonArray nested)
                    foreach (var value in nested) if (value != null) Index(value);
            }
            else if (node is JsonArray array)
                foreach (var value in array) if (value != null) Index(value);
        }

        public bool TryGet(TypeNode.Fqn owner, string name, out (JsonObject Backing, JsonObject Add) declaration)
        {
            RequireBound();
            if (declarations.TryGetValue((owner.Name, name), out var recipe))
            { declaration = (recipe.Backing, recipe.Add); return true; }
            declaration = default; return false;
        }
    }
}
