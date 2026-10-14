using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR branch ownership is fixed here, after splicing, suspension and physical operand normalization.
// Each try body, catch and finally is a distinct region, not one recursive bag of labels. Structured loop
// destinations occupy their declaration's region; CFG destinations occupy their label's exact region.
// ilemit consumes the mandatory `leave` bit without recovering Kotlin semantics or inspecting region membership.
static class ProtectedRegionTransfers
{
    internal sealed record Edge(JsonObject Node, JsonArray[] Source, JsonArray[] Target, int? LoopId = null);
    sealed record Loop(string Label, JsonArray[] Region, int Id);
    static int _loopId;
    static readonly HashSet<string> Loops = new() { "for", "forArray", "forRange", "forEachInline", "repeatInline", "while", "dowhile" };

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        WalkFrames(root, refs);
    }

    static void WalkFrames(JsonNode node, ReferenceMetadataIndex refs, bool inFrame = false, bool constructors = false)
    {
        if (node is JsonObject obj)
        {
            if (TypeJson.IsType(obj)) return;
            if (obj["params"] is JsonArray && obj["body"] is JsonArray body)
            {
                var parts = new[] { obj["preStmts"] as JsonArray, body }.Where(a => a != null).ToArray();
                var returnType = constructors ? TypeJson.Fqn("void") : obj["ret"]
                    ?? throw new InvalidOperationException("bir2cir: executable method lacks its declared physical return type");
                Process(parts, returnType, refs);
                foreach (var child in obj.ToArray())
                    if (child.Value != null) WalkFrames(child.Value, refs, true, child.Key == "ctors");
                return;
            }
            // Static field initializers execute in a synthesized type initializer, not in a serialized method.
            // Their valueBlock/inline loops nevertheless own the same physical transfer contract.
            if (!inFrame && obj["init"] is JsonNode initializer)
            {
                var holder = new JsonArray(initializer.DeepClone());
                Process(new[] { holder }, TypeJson.Fqn("void"), refs);
                obj["init"] = holder[0].DeepClone();
            }
            foreach (var child in obj.ToArray()) if (child.Value != null)
                WalkFrames(child.Value, refs, inFrame || child.Key == "init", child.Key == "ctors");
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray()) if (child != null) WalkFrames(child, refs, inFrame, constructors);
    }

    static void Process(JsonArray[] parts, JsonNode returnType, ReferenceMetadataIndex refs)
    {
        EscapingFinallyLowering.Apply(parts, returnType, refs);
        foreach (var edge in Edges(parts))
        {
            if (K(edge.Node) == "return") continue;
            if (!IsPrefix(edge.Target, edge.Source))
                throw new InvalidOperationException("bir2cir: control transfer enters a different protected region");
            edge.Node["leave"] = edge.Source.Length != edge.Target.Length;
            if (edge.LoopId is int id)
            {
                edge.Node["loopId"] = id;
                edge.Node.Remove("label");
            }
        }
        foreach (var part in parts) DropLoopLabels(part);
    }

    static void DropLoopLabels(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["params"] is JsonArray && obj["body"] is JsonArray) return;
            if (Loops.Contains(K(obj) ?? "")) obj.Remove("label");
            foreach (var child in obj) DropLoopLabels(child.Value);
        }
        else if (node is JsonArray array) foreach (var child in array) DropLoopLabels(child);
    }

    internal static List<Edge> Edges(IEnumerable<JsonArray> parts)
    {
        var labels = new Dictionary<int, JsonArray[]>();
        var edges = new List<Edge>();
        foreach (var part in parts) Visit(part, Array.Empty<JsonArray>(), new(), labels, null);
        foreach (var part in parts) Visit(part, Array.Empty<JsonArray>(), new(), labels, edges);
        return edges;
    }

    static void Visit(JsonNode node, JsonArray[] region, List<Loop> loops,
        Dictionary<int, JsonArray[]> labels, List<Edge> edges)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child != null) Visit(child, region, loops, labels, edges);
            return;
        }
        if (node is not JsonObject obj || TypeJson.IsType(obj)) return;
        // A nested declaration executes in another frame, even when its construction occurs in this body.
        if (obj["params"] is JsonArray && obj["body"] is JsonArray) return;
        var kind = K(obj);
        if (kind == "label" && edges == null) labels.Add(obj["id"].GetValue<int>(), region);
        if (edges != null)
        {
            JsonArray[] target = null;
            int? loopId = null;
            if (kind is "goto" or "brIf") target = labels[obj["id"].GetValue<int>()];
            else if (kind is "break" or "continue")
            {
                var label = Str(obj["label"]);
                var loop = loops.Last(loop => label == null || loop.Label == label);
                target = loop.Region;
                loopId = loop.Id;
            }
            else if (kind == "return") target = Array.Empty<JsonArray>();
            if (target != null) edges.Add(new(obj, region, target, loopId));
        }
        if (kind == "try")
        {
            InRegion(obj["body"] as JsonArray);
            if (obj["catches"] is JsonArray catches)
                foreach (var handler in catches.OfType<JsonObject>()) InRegion(handler["body"] as JsonArray);
            InRegion(obj["finally"] as JsonArray);
            return;
        }
        if (Loops.Contains(kind ?? ""))
        {
            if (edges == null) obj["loopId"] = System.Threading.Interlocked.Increment(ref _loopId);
            foreach (var child in obj) if (child.Key != "body" && child.Value != null) Visit(child.Value, region, loops, labels, edges);
            loops.Add(new(Str(obj["label"]), region, obj["loopId"].GetValue<int>()));
            Visit(obj["body"], region, loops, labels, edges);
            loops.RemoveAt(loops.Count - 1);
            return;
        }
        foreach (var child in obj) if (child.Value != null) Visit(child.Value, region, loops, labels, edges);

        void InRegion(JsonArray body)
        {
            if (body != null) Visit(body, region.Append(body).ToArray(), loops, labels, edges);
        }
    }

    internal static bool IsPrefix(JsonArray[] prefix, JsonArray[] path) => prefix.Length <= path.Length
        && prefix.Select((item, i) => ReferenceEquals(item, path[i])).All(equal => equal);
    internal static string K(JsonNode node) => Str((node as JsonObject)?["k"]);
    internal static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
