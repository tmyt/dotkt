// SHARED across bir2cir / ilemit / dll2klib via a <Compile Link/> (NOT its own project — no
// build-order dependency). The single-source Type read/write helper of the BIR/CIR freeze (#37).
//
// NORMATIVE: docs/bir-cir-spec.md §1 (the Type schema) + §4 (the shared helper API).
// A Type is ALWAYS a JSON object with a `t` discriminator — there is NO bare-string type. Readers
// dispatch(t); they NEVER split/scan a string. This file is the ONE place a Type is parsed/built.
//
// It agrees with kotc.bir.TypeNode for BIR. The phase extensions are all bir2cir-authored CIR facts kotc
// omits: Fn.Clr (the physical delegate family) and the three ECMA signature carriers Ptr, Array.Rank and
// Mod, which exist so a #370 memberRef can spell any signature the target metadata can declare.

#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

/// <summary>
/// The structured type representation shared by the compiler tools. The <see cref="Star"/> variant is a
/// Kotlin projection carrier in BIR/metadata; bir2cir must lower it before CIR emission.
/// `T` in the spec denotes a nested <see cref="TypeNode"/>.
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

    /// <summary>
    /// `tv`: a type variable. <c>Scope</c> ∈ {"type","method"} selects the CLR generic-parameter space
    /// (type → <c>!i</c> GenericTypeParameter, method → <c>!!i</c> GenericMethodParameter). <c>I</c> is
    /// owner-local: for "method" the index in the method's own generic params; for "type" the FLATTENED
    /// index over the enclosing-type nesting chain. The scope disambiguates the two distinct spaces.
    /// </summary>
    public sealed record Tv(string Scope, int I) : TypeNode;

    /// <summary>
    /// `star`: a Kotlin <c>*</c> type projection. kotc preserves it in BIR; bir2cir lowers it to an existential
    /// non-generic view (or an explicit object fallback when no local/reference view exists).
    /// </summary>
    public sealed record Star : TypeNode;

    /// <summary>
    /// `fn`: a function type; <c>Suspend</c> is a flag, <c>Recv</c> is the extension receiver
    /// (subsumes func:/sfunc:). <c>Clr</c> is a CIR-only physical delegate-family decision authored by bir2cir;
    /// kotc's BIR projection always omits it.
    /// <c>Ctx</c> is reference-metadata-only: dll2klib consumes it while serializing the
    /// corresponding context-function shape into KLIB metadata.
    /// how many of the function type's LEADING arguments are Kotlin CONTEXT parameters. `context(A) B.(D) -&gt; E`
    /// restores as <c>Ctx=[A]</c>, <c>Recv=B</c>, <c>Params=[D]</c>. kotc's BIR carries the same fact as the
    /// declaration-slot key `ctxFnType` instead, because a type node is rebuilt by many lowering passes.
    /// </summary>
    public sealed record Fn(bool Suspend, TypeNode Ret, TypeNode[] Params, TypeNode? Recv = null, string? Clr = null,
        TypeNode[]? Ctx = null) : TypeNode
    {
        public bool Equals(Fn? o) =>
            o is not null && Suspend == o.Suspend && Ret == o.Ret && SeqEq(Params, o.Params)
            && Recv == o.Recv && Clr == o.Clr && SeqEq(Ctx ?? System.Array.Empty<TypeNode>(), o.Ctx ?? System.Array.Empty<TypeNode>());
        public override int GetHashCode() => System.HashCode.Combine(Suspend, Ret, Params.Length, Recv, Clr, Ctx?.Length ?? 0);

        /// <summary>
        /// The delegate ARG list: an extension receiver (`P.() -> R`) is the delegate's FIRST argument on the CLR
        /// (`P.() -> R` = `KAction`1[P]` / `Func&lt;P,R&gt;`), so <c>Recv</c> — when present — is prepended to
        /// <c>Params</c>. Every delegate-shape reader (ilemit FuncType/SigTokenOf/FuncArity, mentions-tv) uses this
        /// so the emitted delegate + overload token match whether kotc kept the receiver in <c>Recv</c> (a restored
        /// `P.() -> R` param type) or flat in <c>Params</c> (a lambda-value closure). Non-receiver fn -> just Params.
        /// </summary>
        public TypeNode[] DelegateParams
        {
            get
            {
                if (Recv is null) return Params;
                var all = new TypeNode[Params.Length + 1];
                all[0] = Recv;
                System.Array.Copy(Params, 0, all, 1, Params.Length);
                return all;
            }
        }
    }

    /// <summary>`nullable`: <c>T?</c> (NullableAttribute=2).</summary>
    public sealed record Nullable(TypeNode Of) : TypeNode;

    /// <summary>
    /// `oblivious`: <c>T!</c> — an NRT-oblivious reference type (NullableAttribute=0), the flexible/platform
    /// <c>(T..T?)</c> (spec §1 tri-state nullability). A sibling of <see cref="Nullable"/> with the same
    /// <c>{of:T}</c> shape. dll2klib META emits it for a .NET member with no NullableAttribute; the kotc
    /// frontend maps it to a <c>ConeFlexibleType</c>. It is frontend-only — resolved to not-null/nullable
    /// before the backend — so bir2cir/ilemit never emit it (they only Read it transparently).
    /// </summary>
    public sealed record Oblivious(TypeNode Of) : TypeNode;

    /// <summary>
    /// `array`: <c>Array&lt;T&gt;</c>. An ABSENT <c>Rank</c> is the ECMA SZARRAY <c>T[]</c> — the vector, and
    /// the only array shape Kotlin source can spell. A PRESENT rank is the CIR-only general ARRAY: rank 2 or
    /// more is <c>T[,…]</c>, and rank 1 is the rare single-dimensional non-vector <c>T[*]</c>, which ECMA
    /// treats as a different type from <c>T[]</c> — reflection tells them apart with <c>IsSZArray</c>, and an
    /// external member may declare both. Every one of these distinctions exists for the same reason: without
    /// it two overloads produce one signature, and a reference that cannot tell them apart selects by luck.
    /// </summary>
    public sealed record Array(TypeNode Elem, int Rank = 1, bool SzArray = true) : TypeNode
    {
        /// <summary>The general (non-vector) array of the given rank — <c>T[*]</c> at rank 1.</summary>
        public static Array General(TypeNode elem, int rank) => new(elem, rank, SzArray: false);
    }

    /// <summary>The CLR's own limit on array rank; a document beyond it describes no representable type.</summary>
    public const int MaxArrayRank = 32;

    /// <summary>`byRef`: a CLR by-ref <c>ref T</c>.</summary>
    public sealed record ByRef(TypeNode Of) : TypeNode;

    /// <summary>
    /// `ptr`: a CLR unmanaged pointer <c>T*</c>. CIR-only: Kotlin source cannot spell one, but an external
    /// member can declare it, and without this variant such a parameter degrades to the FQN string
    /// <c>"System.Int32*"</c> — an identity that names no type.
    /// </summary>
    public sealed record Ptr(TypeNode Of) : TypeNode;

    /// <summary>
    /// `mod`: an ECMA custom modifier applied AT ITS SIGNATURE POSITION (II.7.1.1). <c>Req</c> selects
    /// modreq (true) from modopt (false), <c>M</c> is the modifier type and <c>Of</c> the modified type.
    /// CIR-only, and part of member identity: <c>void V(in DateTime)</c> and <c>void V(DateTime)</c> differ
    /// only by <c>modreq(InAttribute)</c> on a by-ref parameter, so a member reference that drops the
    /// modifier cannot select between them. Nesting (rather than a sidecar list) is what keeps the modifier
    /// attached to the exact position it modifies.
    /// </summary>
    public sealed record Mod(bool Req, TypeNode M, TypeNode Of) : TypeNode;

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
        string t = e.GetProperty("t").GetString()!;
        switch (t)
        {
            case "fqn":
                return new Fqn(
                    e.GetProperty("name").GetString()!,
                    e.TryGetProperty("args", out var args) ? ReadArray(args) : null);
            case "tv":
                return new Tv(
                    e.GetProperty("scope").GetString()!,
                    e.GetProperty("i").GetInt32());
            case "star":
                return new Star();
            case "fn":
                return new Fn(
                    e.GetProperty("suspend").GetBoolean(),
                    Read(e.GetProperty("ret")),
                    ReadArray(e.GetProperty("params")),
                    e.TryGetProperty("recv", out var recv) ? Read(recv) : null,
                    e.TryGetProperty("clr", out var clr) ? clr.GetString() : null,
                    e.TryGetProperty("ctx", out var ctxA) ? ReadArray(ctxA) : null);
            case "nullable":
                return new Nullable(Read(e.GetProperty("of")));
            case "oblivious":
                return new Oblivious(Read(e.GetProperty("of")));
            case "array":
            {
                // An absent `rank` is the vector. A PRESENT one names the general array — including rank 1,
                // the non-vector `T[*]` that ECMA keeps distinct from `T[]`. The CLR caps rank at 32, so a
                // value outside that describes nothing a metadata writer could emit.
                if (!e.TryGetProperty("rank", out var rk))
                    return new Array(Read(e.GetProperty("elem")));
                int rank = rk.GetInt32();
                if (rank < 1 || rank > MaxArrayRank)
                    throw new FormatException($"array.rank must be between 1 and {MaxArrayRank}, got {rank}");
                return Array.General(Read(e.GetProperty("elem")), rank);
            }
            case "byRef":
                return new ByRef(Read(e.GetProperty("of")));
            case "ptr":
                return new Ptr(Read(e.GetProperty("of")));
            case "mod":
                return new Mod(e.GetProperty("req").GetBoolean(), Read(e.GetProperty("m")), Read(e.GetProperty("of")));
            default:
                throw new InvalidOperationException();
        }
    }

    private static TypeNode[] ReadArray(JsonElement e)
    {
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
                return new JsonObject { ["t"] = "tv", ["scope"] = v.Scope, ["i"] = v.I };
            case Star:
                return new JsonObject { ["t"] = "star" };
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
                if (fn.Clr is not null) o["clr"] = fn.Clr;
                if (fn.Ctx is { Length: > 0 }) o["ctx"] = WriteArray(fn.Ctx);
                return o;
            }
            case Nullable n:
                return new JsonObject { ["t"] = "nullable", ["of"] = Write(n.Of) };
            case Oblivious ob:
                return new JsonObject { ["t"] = "oblivious", ["of"] = Write(ob.Of) };
            case Array a:
            {
                if (a.Rank < 1 || a.Rank > MaxArrayRank)
                    throw new ArgumentException($"array rank must be between 1 and {MaxArrayRank}, got {a.Rank}");
                if (a.SzArray && a.Rank != 1)
                    throw new ArgumentException($"an SZ array has rank 1 by definition, got {a.Rank}");
                var o = new JsonObject { ["t"] = "array", ["elem"] = Write(a.Elem) };
                // The vector omits its rank; every general array states one, rank 1 included — that is the
                // only thing separating `T[*]` from `T[]` in this document.
                if (!a.SzArray) o["rank"] = a.Rank;
                return o;
            }
            case ByRef b:
                return new JsonObject { ["t"] = "byRef", ["of"] = Write(b.Of) };
            case Ptr p:
                return new JsonObject { ["t"] = "ptr", ["of"] = Write(p.Of) };
            case Mod m:
                return new JsonObject { ["t"] = "mod", ["req"] = m.Req, ["m"] = Write(m.M), ["of"] = Write(m.Of) };
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

    /// <summary>Compact canonical JSON string of a type. A BIR node (Fn.Clr absent) matches kotc byte-for-byte.</summary>
    public static string ToJson(TypeNode t) =>
        Write(t).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>Parse a canonical type JSON string back into a <see cref="TypeNode"/>.</summary>
    public static TypeNode Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, BirJson.DocOptions);
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
    public const string MsgPackV1 = "bir-msgpack/1";

    public static byte[] EncodeBody(string version, JsonNode body)
    {
        return version switch
        {
            JsonV1 => Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions { WriteIndented = false })),
            MsgPackV1 => throw new NotSupportedException("bir-msgpack/1 is not implemented"),
            _ => throw new NotSupportedException(),
        };
    }

    public static JsonNode DecodeBody(string version, byte[] content)
    {
        return version switch
        {
            JsonV1 => JsonNode.Parse(Encoding.UTF8.GetString(content), documentOptions: BirJson.DocOptions)!,
            MsgPackV1 => throw new NotSupportedException("bir-msgpack/1 is not implemented"),
            _ => throw new NotSupportedException(),
        };
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
            (new TypeNode.Fqn("Bounded", new TypeNode[] { new TypeNode.Star() }),
                "{\"t\":\"fqn\",\"name\":\"Bounded\",\"args\":[{\"t\":\"star\"}]}"),
            (new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.String"), new TypeNode[] { new TypeNode.Fqn("kotlin.Int") }),
                "{\"t\":\"fn\",\"suspend\":false,\"ret\":{\"t\":\"fqn\",\"name\":\"kotlin.String\"},\"params\":[{\"t\":\"fqn\",\"name\":\"kotlin.Int\"}]}"),
            // suspend Foo<T>.()->T?
            (new TypeNode.Fn(true,
                    new TypeNode.Nullable(new TypeNode.Tv("type", 0)),
                    System.Array.Empty<TypeNode>(),
                    new TypeNode.Fqn("Foo", new TypeNode[] { new TypeNode.Tv("type", 0) })),
                "{\"t\":\"fn\",\"suspend\":true,\"ret\":{\"t\":\"nullable\",\"of\":{\"t\":\"tv\",\"scope\":\"type\",\"i\":0}},\"params\":[],\"recv\":{\"t\":\"fqn\",\"name\":\"Foo\",\"args\":[{\"t\":\"tv\",\"scope\":\"type\",\"i\":0}]}}"),
            // array + byref (cover the remaining variants)
            (new TypeNode.Array(new TypeNode.ByRef(new TypeNode.Fqn("kotlin.Long"))),
                "{\"t\":\"array\",\"elem\":{\"t\":\"byRef\",\"of\":{\"t\":\"fqn\",\"name\":\"kotlin.Long\"}}}"),
        };

        // CIR-ONLY signature carriers (#370). kotc never emits these, so they are deliberately NOT part of the
        // cross-language fixture above: only bir2cir authors them and only ilemit reads them.
        var cirOnly = new (TypeNode node, string json)[]
        {
            (new TypeNode.Ptr(new TypeNode.Fqn("System.Int32")),
                "{\"t\":\"ptr\",\"of\":{\"t\":\"fqn\",\"name\":\"System.Int32\"}}"),
            // int[,] — a DIFFERENT member signature from int[]; rank is absent for an SZ array.
            (TypeNode.Array.General(new TypeNode.Fqn("System.Int32"), 2),
                "{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"rank\":2}"),
            // int[*] — the single-dimensional NON-vector. ECMA keeps it distinct from int[], and a stated
            // rank of 1 is the only thing that says so.
            (TypeNode.Array.General(new TypeNode.Fqn("System.Int32"), 1),
                "{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"rank\":1}"),
            // `in DateTime` = modreq(InAttribute) ref DateTime — the modifier sits at the position it modifies.
            (new TypeNode.Mod(true, new TypeNode.Fqn("System.Runtime.InteropServices.InAttribute"),
                    new TypeNode.ByRef(new TypeNode.Fqn("System.DateTime"))),
                "{\"t\":\"mod\",\"req\":true,\"m\":{\"t\":\"fqn\",\"name\":\"System.Runtime.InteropServices.InAttribute\"},\"of\":{\"t\":\"byRef\",\"of\":{\"t\":\"fqn\",\"name\":\"System.DateTime\"}}}"),
            (new TypeNode.Mod(false, new TypeNode.Fqn("System.Runtime.CompilerServices.IsConst"),
                    new TypeNode.Fqn("System.Int32")),
                "{\"t\":\"mod\",\"req\":false,\"m\":{\"t\":\"fqn\",\"name\":\"System.Runtime.CompilerServices.IsConst\"},\"of\":{\"t\":\"fqn\",\"name\":\"System.Int32\"}}"),
        };

        int n = 0;
        var all = new List<(TypeNode node, string json)>(cases);
        all.AddRange(cirOnly);
        foreach (var (node, json) in all)
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

        // A vector and a rank-1 general array are DIFFERENT types, and the document distinguishes them by
        // whether a rank is stated at all — so neither may be read as the other.
        if (TypeNode.Parse("{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"rank\":1}")
            == TypeNode.Parse("{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"System.Int32\"}}"))
            throw new Exception("[C# TypeNode] `T[*]` and `T[]` must not compare equal");
        try
        {
            TypeNode.Parse("{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"rank\":0}");
            throw new Exception("[C# TypeNode] expected a FormatException for array rank 0");
        }
        catch (FormatException) { /* expected */ }
        try
        {
            TypeNode.Parse("{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"rank\":99999}");
            throw new Exception("[C# TypeNode] expected a FormatException for an array rank beyond the CLR limit");
        }
        catch (FormatException) { /* expected */ }
        // An SZ array must NOT acquire a rank key on the way out, or every existing array node changes bytes.
        if (TypeNode.ToJson(new TypeNode.Array(new TypeNode.Fqn("System.Int32")))
            != "{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"System.Int32\"}}")
            throw new Exception("[C# TypeNode] SZ array must serialize without a rank key");

        // Carrier round-trip (bir-json/1: UTF8 <-> JSON).
        var body = TypeNode.Write(cases[1].node);
        byte[] enc = BirCarrier.EncodeBody(BirCarrier.JsonV1, body);
        var dec = BirCarrier.DecodeBody(BirCarrier.JsonV1, enc);
        if (TypeNode.Read(JsonDocument.Parse(dec.ToJsonString()).RootElement) != cases[1].node)
            throw new Exception("[C# TypeNode] carrier round-trip mismatch");

        Console.WriteLine($"[C# TypeNode] self-test OK ({n} fixture cases incl. {cirOnly.Length} CIR-only + carrier)");
    }
}
