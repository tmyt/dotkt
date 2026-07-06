// SHARED across bir2cir / ilemit / facadegen via a <Compile Link/> (NOT its own project — no
// build-order dependency). The single-source Type read/write helper of the BIR/CIR freeze (#37).
//
// NORMATIVE: docs/bir-cir-spec.md §1 (the Type schema) + §4 (the shared helper API).
// A Type is ALWAYS a JSON object with a `t` discriminator — there is NO bare-string type. Readers
// dispatch(t); they NEVER split/scan a string. This file is the ONE place a Type is parsed/built.
//
// This file is ADDITIVE (phase 1b): it defines the frozen contract in code. It is not yet wired to
// the emit/consume paths (phases 2-5). It must byte-for-byte agree with kotc.bir.TypeNode (Kotlin).

#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

/// <summary>
/// The universal type representation (spec §1). A sealed hierarchy of six variants — every Kotlin/CLR
/// type identity is exactly one of these. `T` in the spec denotes a nested <see cref="TypeNode"/>.
/// </summary>
public abstract record TypeNode
{
    // --- Variants (spec §1 table) --------------------------------------------------------------
    // Custom Equals/GetHashCode are provided per array-bearing variant so that structural `==`
    // (which records give for scalars but NOT for array members) holds — the round-trip self-test
    // relies on `Read(Write(node)) == node`.

    /// <summary>`fqn`: a named type — a PURE Kotlin/CLR FQN identity; <c>Args</c> = generic application.</summary>
    public sealed record Fqn(string Name, TypeNode[]? Args = null) : TypeNode
    {
        public bool Equals(Fqn? o) => o is not null && Name == o.Name && SeqEq(Args, o.Args);
        public override int GetHashCode() => System.HashCode.Combine(Name, Args?.Length ?? -1);
    }

    /// <summary>`tv`: a type variable, POSITIONAL index into the owning generic decl's type-parameter list.</summary>
    public sealed record Tv(int I) : TypeNode;

    /// <summary>`fn`: a function type; <c>Suspend</c> is a flag, <c>Recv</c> is the extension receiver (subsumes func:/sfunc:).</summary>
    public sealed record Fn(bool Suspend, TypeNode Ret, TypeNode[] Params, TypeNode? Recv = null) : TypeNode
    {
        public bool Equals(Fn? o) =>
            o is not null && Suspend == o.Suspend && Ret == o.Ret && SeqEq(Params, o.Params) && Recv == o.Recv;
        public override int GetHashCode() => System.HashCode.Combine(Suspend, Ret, Params.Length, Recv);
    }

    /// <summary>`nullable`: <c>T?</c>.</summary>
    public sealed record Nullable(TypeNode Of) : TypeNode;

    /// <summary>`array`: <c>Array&lt;T&gt;</c> (this-assembly array).</summary>
    public sealed record Array(TypeNode Elem) : TypeNode;

    /// <summary>`byref`: a CLR by-ref <c>ref T</c>.</summary>
    public sealed record Byref(TypeNode Of) : TypeNode;

    private static bool SeqEq(TypeNode[]? a, TypeNode[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;   // recurses through each variant's structural ==
        return true;
    }

    // --- Read: JsonElement -> TypeNode (dispatch on `t`, recursive, NO string-splitting) -------
    public static TypeNode Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Type must be a JSON object, got {e.ValueKind}");
        string t = e.GetProperty("t").GetString()
                   ?? throw new FormatException("Type node missing `t` discriminator");
        switch (t)
        {
            case "fqn":
                return new Fqn(
                    e.GetProperty("name").GetString() ?? throw new FormatException("fqn missing name"),
                    e.TryGetProperty("args", out var args) ? ReadArray(args) : null);
            case "tv":
                return new Tv(e.GetProperty("i").GetInt32());
            case "fn":
                return new Fn(
                    e.GetProperty("suspend").GetBoolean(),
                    Read(e.GetProperty("ret")),
                    ReadArray(e.GetProperty("params")),
                    e.TryGetProperty("recv", out var recv) ? Read(recv) : null);
            case "nullable":
                return new Nullable(Read(e.GetProperty("of")));
            case "array":
                return new Array(Read(e.GetProperty("elem")));
            case "byref":
                return new Byref(Read(e.GetProperty("of")));
            default:
                throw new FormatException($"unknown Type discriminator `t`=\"{t}\"");
        }
    }

    private static TypeNode[] ReadArray(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array)
            throw new FormatException($"expected a JSON array of Types, got {e.ValueKind}");
        var list = new List<TypeNode>(e.GetArrayLength());
        foreach (var item in e.EnumerateArray()) list.Add(Read(item));
        return list.ToArray();
    }

    // --- Write: TypeNode -> JsonNode (insertion order = field order; required first, optional last) ---
    public static JsonNode Write(TypeNode t)
    {
        switch (t)
        {
            case Fqn f:
            {
                var o = new JsonObject { ["t"] = "fqn", ["name"] = f.Name };
                if (f.Args is not null) o["args"] = WriteArray(f.Args);
                return o;
            }
            case Tv v:
                return new JsonObject { ["t"] = "tv", ["i"] = v.I };
            case Fn fn:
            {
                var o = new JsonObject
                {
                    ["t"] = "fn",
                    ["suspend"] = fn.Suspend,
                    ["ret"] = Write(fn.Ret),
                    ["params"] = WriteArray(fn.Params),
                };
                if (fn.Recv is not null) o["recv"] = Write(fn.Recv);
                return o;
            }
            case Nullable n:
                return new JsonObject { ["t"] = "nullable", ["of"] = Write(n.Of) };
            case Array a:
                return new JsonObject { ["t"] = "array", ["elem"] = Write(a.Elem) };
            case Byref b:
                return new JsonObject { ["t"] = "byref", ["of"] = Write(b.Of) };
            default:
                throw new ArgumentException($"unknown TypeNode variant {t.GetType().Name}");
        }
    }

    private static JsonArray WriteArray(TypeNode[] ts)
    {
        var arr = new JsonArray();
        foreach (var t in ts) arr.Add(Write(t));
        return arr;
    }

    /// <summary>Compact canonical JSON string of a type — must match kotc's TypeNode.toJson byte-for-byte.</summary>
    public static string ToJson(TypeNode t) =>
        Write(t).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>Parse a canonical type JSON string back into a <see cref="TypeNode"/>.</summary>
    public static TypeNode Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc.RootElement);
    }
}

/// <summary>
/// The carrier codec (spec §0). <c>version</c> selects codec+schema: <c>"bir-json/1"</c> = UTF8(JSON) today;
/// a future <c>"bir-msgpack/1"</c> branch is a NotSupported stub. A single Encode/Decode pair dispatches.
/// </summary>
public static class BirCarrier
{
    public const string JsonV1 = "bir-json/1";

    public static byte[] EncodeBody(string version, JsonNode body)
    {
        switch (version)
        {
            case JsonV1:
                return Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            default:
                if (version.StartsWith("bir-msgpack/"))
                    throw new NotSupportedException($"carrier codec `{version}` not yet implemented (msgpack is a future branch)");
                throw new NotSupportedException($"unknown carrier version `{version}`");
        }
    }

    public static JsonNode DecodeBody(string version, byte[] content)
    {
        switch (version)
        {
            case JsonV1:
                return JsonNode.Parse(Encoding.UTF8.GetString(content))
                       ?? throw new FormatException("carrier body decoded to a null JSON node");
            default:
                if (version.StartsWith("bir-msgpack/"))
                    throw new NotSupportedException($"carrier codec `{version}` not yet implemented (msgpack is a future branch)");
                throw new NotSupportedException($"unknown carrier version `{version}`");
        }
    }
}

/// <summary>
/// Round-trip self-test of the shared TypeNode contract. Proves Read(Write(node)) == node for every
/// variant and that both directions agree with the shared cross-language fixture (spec §1). Called by
/// a throwaway harness during verification; NOT a Main (the shared file must not own an entry point).
/// </summary>
public static class TypeNodeSelfTest
{
    public static void Run()
    {
        // The shared cross-language fixture (spec §1 examples; canonical field order = required first,
        // optional last). kotc.bir.TypeNode's self-test validates the SAME strings.
        var cases = new (TypeNode node, string json)[]
        {
            (new TypeNode.Fqn("kotlin.Int"),
                "{\"t\":\"fqn\",\"name\":\"kotlin.Int\"}"),
            (new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { new TypeNode.Fqn("kotlin.Int") }),
                "{\"t\":\"fqn\",\"name\":\"kotlin.collections.List\",\"args\":[{\"t\":\"fqn\",\"name\":\"kotlin.Int\"}]}"),
            (new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.String"), new TypeNode[] { new TypeNode.Fqn("kotlin.Int") }),
                "{\"t\":\"fn\",\"suspend\":false,\"ret\":{\"t\":\"fqn\",\"name\":\"kotlin.String\"},\"params\":[{\"t\":\"fqn\",\"name\":\"kotlin.Int\"}]}"),
            // suspend Foo<T>.()->T?
            (new TypeNode.Fn(true,
                    new TypeNode.Nullable(new TypeNode.Tv(0)),
                    System.Array.Empty<TypeNode>(),
                    new TypeNode.Fqn("Foo", new TypeNode[] { new TypeNode.Tv(0) })),
                "{\"t\":\"fn\",\"suspend\":true,\"ret\":{\"t\":\"nullable\",\"of\":{\"t\":\"tv\",\"i\":0}},\"params\":[],\"recv\":{\"t\":\"fqn\",\"name\":\"Foo\",\"args\":[{\"t\":\"tv\",\"i\":0}]}}"),
            // array + byref (cover the remaining variants)
            (new TypeNode.Array(new TypeNode.Byref(new TypeNode.Fqn("kotlin.Long"))),
                "{\"t\":\"array\",\"elem\":{\"t\":\"byref\",\"of\":{\"t\":\"fqn\",\"name\":\"kotlin.Long\"}}}"),
        };

        int n = 0;
        foreach (var (node, json) in cases)
        {
            // Write must equal the canonical fixture string byte-for-byte.
            string got = TypeNode.ToJson(node);
            if (got != json)
                throw new Exception($"[C# TypeNode] Write mismatch:\n  expected {json}\n  got      {got}");
            // Read(fixture) must reconstruct the node.
            var parsed = TypeNode.Parse(json);
            if (parsed != node)
                throw new Exception($"[C# TypeNode] Read mismatch for {json}: got {parsed}");
            // Read(Write(node)) == node (the core round-trip property).
            if (TypeNode.Parse(TypeNode.ToJson(node)) != node)
                throw new Exception($"[C# TypeNode] round-trip mismatch for {json}");
            n++;
        }

        // Carrier round-trip (bir-json/1: UTF8 <-> JSON).
        var body = TypeNode.Write(cases[1].node);
        byte[] enc = BirCarrier.EncodeBody(BirCarrier.JsonV1, body);
        var dec = BirCarrier.DecodeBody(BirCarrier.JsonV1, enc);
        if (TypeNode.Read(JsonDocument.Parse(dec.ToJsonString()).RootElement) != cases[1].node)
            throw new Exception("[C# TypeNode] carrier round-trip mismatch");

        // The msgpack branch is a stub for now.
        try { BirCarrier.EncodeBody("bir-msgpack/1", body); throw new Exception("expected NotSupported for msgpack"); }
        catch (NotSupportedException) { /* expected */ }

        Console.WriteLine($"[C# TypeNode] self-test OK ({n} fixture cases + carrier + msgpack-stub)");
    }
}
