// SHARED across bir2cir / ilemit / dll2klib via a <Compile Link/> (NOT its own project — no
// build-order dependency). The single-source Type read/write helper of the BIR/CIR freeze (#37).
//
// NORMATIVE: docs/bir-cir-spec.md §1 (the Type schema) + §4 (the shared helper API).
// A Type is ALWAYS a JSON object with a `t` discriminator — there is NO bare-string type. Readers
// dispatch(t); they NEVER split/scan a string. Structural data and streaming codecs are schema-generated;
// semantic helpers and the temporary DOM adapters live here.
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
public abstract partial record TypeNode
{
    // Structural declarations are schema-generated in TypeNode.Model.Generated.cs. Equality and the delegate
    // argument view remain semantic operations here; source classifier metadata is not part of a physical key.

    /// <summary>`fqn`: a named type — a PURE Kotlin/CLR FQN identity; <c>Args</c> = generic application.</summary>
    public sealed partial record Fqn
    {
        public bool Equals(Fqn? o) => o is not null && Name == o.Name && SeqEq(Args, o.Args);
        public override int GetHashCode() => System.HashCode.Combine(Name, Args?.Length ?? -1);
    }

    /// <summary>
    /// `fn`: a function type; <c>Suspend</c> is a flag, <c>Recv</c> is the extension receiver
    /// (subsumes func:/sfunc:). <c>Clr</c> is a CIR-only physical delegate-family decision authored by bir2cir;
    /// kotc's BIR projection always omits it.
    /// <c>Ctx</c> is reference-metadata-only: dll2klib consumes it while serializing the
    /// corresponding context-function shape into KLIB metadata. It states the function's leading Kotlin
    /// context-parameter types: `context(A) B.(D) -&gt; E`
    /// restores as <c>Ctx=[A]</c>, <c>Recv=B</c>, <c>Params=[D]</c>. kotc's BIR carries the same fact as the
    /// declaration-slot key `ctxFnType` instead, because a type node is rebuilt by many lowering passes.
    /// </summary>
    public sealed partial record Fn
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

    /// <summary>
    /// `array`: <c>Array&lt;T&gt;</c>. An ABSENT <c>Rank</c> is the ECMA SZARRAY <c>T[]</c> — the vector, and
    /// the only array shape Kotlin source can spell. A PRESENT rank is the CIR-only general ARRAY: rank 2 or
    /// more is <c>T[,…]</c>, and rank 1 is the rare single-dimensional non-vector <c>T[*]</c>, which ECMA
    /// treats as a different type from <c>T[]</c> — reflection tells them apart with <c>IsSZArray</c>, and an
    /// external member may declare both. Every one of these distinctions exists for the same reason: without
    /// it two overloads produce one signature, and a reference that cannot tell them apart selects by luck.
    /// </summary>
    public sealed partial record Array
    {
        /// <summary>The general (non-vector) array of the given rank — <c>T[*]</c> at rank 1.</summary>
        public static Array General(TypeNode elem, int rank) => new(elem, rank, SzArray: false);
    }

    /// <summary>The CLR's own limit on array rank; a document beyond it describes no representable type.</summary>
    public const int MaxArrayRank = 32;

    /// <summary>
    /// `fnPtr`: an exact ECMA function-pointer signature, not a Kotlin function or CLR delegate.
    /// The raw signature header, generic arity and required parameter count retain calling-convention
    /// and sentinel facts independently of the recursively modifier-bearing return and parameter types.
    /// CIR-only: bir2cir resolves the corresponding Kotlin interop vocabulary into this physical type.
    /// </summary>
    public sealed partial record FnPtr
    {
        public bool Equals(FnPtr? other) => other is not null
            && CallingConvention == other.CallingConvention && GenericArity == other.GenericArity
            && RequiredParams == other.RequiredParams && Ret == other.Ret && SeqEq(Params, other.Params);
        public override int GetHashCode() => System.HashCode.Combine(
            CallingConvention, GenericArity, RequiredParams, Ret, Params.Length);
    }

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
                    e.TryGetProperty("args", out var args) ? ReadArray(args) : null)
                {
                    Classifier = e.TryGetProperty("classifier", out var source)
                        ? new SourceClassifier(source.GetProperty("packageName").GetString()!,
                            source.GetProperty("ownerPath").EnumerateArray().Select(p => p.GetString()!).ToArray(),
                            source.GetProperty("name").GetString()!)
                        : null,
                };
            case "tv":
                return new Tv(
                    e.GetProperty("scope").GetString() ?? throw new FormatException("tv missing scope"),
                    e.GetProperty("i").GetInt32());
            case "star":
                return new Star();
            case "projection":
            {
                var variance = e.GetProperty("variance").GetString();
                if (variance is not ("in" or "out"))
                    throw new FormatException($"projection.variance must be `in` or `out`, got `{variance}`");
                return new Projection(variance, Read(e.GetProperty("of")));
            }
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
            case "fnPtr":
                return ReadFunctionPointer(e.GetProperty("cc").GetInt32(), e.GetProperty("genericArity").GetInt32(),
                    e.GetProperty("requiredParams").GetInt32(), Read(e.GetProperty("ret")), ReadArray(e.GetProperty("params")));
            case "mod":
                return new Mod(e.GetProperty("req").GetBoolean(), Read(e.GetProperty("m")), Read(e.GetProperty("of")));
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

    /// <summary>Read an already-materialized, mutable JSON tree without serializing the type.</summary>
    public static TypeNode Read(JsonNode? node)
    {
        // Customized JsonValues can serialize as objects (or arrays), not just CLR scalars.
        if (node is not JsonObject) node = node?.DeepClone();
        if (node is not JsonObject e)
            throw new FormatException($"Type must be a JSON object, got {node?.GetValueKind() ?? JsonValueKind.Null}");
        string t = ReadString(Required(e, "t"))
            ?? throw new FormatException("Type node missing `t` discriminator");
        switch (t)
        {
            case "fqn":
                return new Fqn(
                    ReadString(Required(e, "name")) ?? throw new FormatException("fqn missing name"),
                    TryProperty(e, "args", out var args) ? ReadArray(args) : null)
                {
                    Classifier = TryProperty(e, "classifier", out var source)
                        ? ReadClassifier(source) : null,
                };
            case "tv":
                return new Tv(ReadString(Required(e, "scope")) ?? throw new FormatException("tv missing scope"),
                    ReadInt32(Required(e, "i")));
            case "star":
                return new Star();
            case "projection":
            {
                var variance = ReadString(Required(e, "variance"));
                if (variance is not ("in" or "out"))
                    throw new FormatException($"projection.variance must be `in` or `out`, got `{variance}`");
                return new Projection(variance, Read(Required(e, "of")));
            }
            case "fn":
                return new Fn(ReadBoolean(Required(e, "suspend")), Read(Required(e, "ret")),
                    ReadArray(Required(e, "params")),
                    TryProperty(e, "recv", out var recv) ? Read(recv) : null,
                    TryProperty(e, "clr", out var clr) ? ReadString(clr) : null,
                    TryProperty(e, "ctx", out var ctx) ? ReadArray(ctx) : null);
            case "nullable":
                return new Nullable(Read(Required(e, "of")));
            case "oblivious":
                return new Oblivious(Read(Required(e, "of")));
            case "array":
            {
                if (!TryProperty(e, "rank", out var rk))
                    return new Array(Read(Required(e, "elem")));
                int rank = ReadInt32(rk);
                if (rank < 1 || rank > MaxArrayRank)
                    throw new FormatException($"array.rank must be between 1 and {MaxArrayRank}, got {rank}");
                return Array.General(Read(Required(e, "elem")), rank);
            }
            case "byRef":
                return new ByRef(Read(Required(e, "of")));
            case "ptr":
                return new Ptr(Read(Required(e, "of")));
            case "fnPtr":
                return ReadFunctionPointer(ReadInt32(Required(e, "cc")), ReadInt32(Required(e, "genericArity")),
                    ReadInt32(Required(e, "requiredParams")), Read(Required(e, "ret")), ReadArray(Required(e, "params")));
            case "mod":
                return new Mod(ReadBoolean(Required(e, "req")), Read(Required(e, "m")), Read(Required(e, "of")));
            default:
                throw new FormatException($"unknown Type discriminator `t`=\"{t}\"");
        }
    }

    internal static FnPtr ReadFunctionPointer(int convention, int arity, int required, TypeNode result, TypeNode[] parameters)
    {
        if (convention is < 0 or > 255 || arity < 0 || required < 0 || required > parameters.Length)
            throw new FormatException("Function-pointer header, arity or required parameter count is malformed");
        return new FnPtr(convention, arity, required, result, parameters);
    }

    // These are model/wire projections and cross-field invariants, not type lowering.
    internal static Array ArrayFromWire(TypeNode elem, int? rank)
        => rank is int r ? Array.General(elem, r) : new Array(elem);

    internal static void ValidateArrayWire(Array node)
    {
        if (node.Rank < 1 || node.Rank > MaxArrayRank || node.SzArray && node.Rank != 1)
            throw new ArgumentException("Malformed array shape");
    }

    internal static void ValidateFunctionPointerWire(FnPtr node)
    {
        if (node.CallingConvention is < 0 or > 255 || node.GenericArity < 0
            || node.RequiredParams < 0 || node.RequiredParams > node.Params.Length)
            throw new ArgumentException("Malformed function-pointer shape");
    }

    private static JsonNode? Required(JsonObject e, string name) =>
        TryProperty(e, name, out var value) ? value : throw new KeyNotFoundException(name);

    private static bool TryProperty(JsonObject e, string name, out JsonNode? value)
    {
        // JsonElement property names are ordinal, regardless of JsonNodeOptions on a generated tree.
        if (e.Options?.PropertyNameCaseInsensitive != true) return e.TryGetPropertyValue(name, out value);
        foreach (var property in e)
            if (property.Key == name) { value = property.Value; return true; }
        value = null;
        return false;
    }

    // DeepClone normalizes customized JsonValues using their own serialization contract, while
    // ordinary scalar clones retain their value. Reading the original CLR value directly can bypass
    // custom converters or number handling. Only scalar values, never whole types, take this path.
    private static string? ReadString(JsonNode? node)
    {
        node = node?.DeepClone();
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var s)
            && s.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') < 0) return s;
        // JSON writing replaces unpaired UTF-16 surrogates; do not preserve an invalid CLR string.
        return JsonSerializer.SerializeToElement(node).GetString();
    }

    private static bool ReadBoolean(JsonNode? node)
    {
        node = node?.DeepClone();
        return node is JsonValue value && value.TryGetValue<bool>(out var b) ? b
            : JsonSerializer.SerializeToElement(node).GetBoolean();
    }

    private static int ReadInt32(JsonNode? node)
    {
        node = node?.DeepClone();
        return node is JsonValue value && value.TryGetValue<int>(out var i) ? i
            : JsonSerializer.SerializeToElement(node).GetInt32();
    }

    private static SourceClassifier ReadClassifier(JsonNode? node)
    {
        if (node is not JsonObject) node = node?.DeepClone();
        if (node is not JsonObject source)
            throw new InvalidOperationException("classifier must be an object with an ownerPath array");
        var ownerPath = Required(source, "ownerPath");
        var owners = ownerPath as JsonArray ?? ownerPath?.DeepClone() as JsonArray
            ?? throw new InvalidOperationException("classifier.ownerPath must be an array");
        // Match the existing JsonElement classifier contract, including its nullable string values.
        return new SourceClassifier(ReadString(Required(source, "packageName"))!,
            owners.Select(p => ReadString(p)!).ToArray(), ReadString(Required(source, "name"))!);
    }

    private static TypeNode[] ReadArray(JsonNode? node)
    {
        if (node is not JsonArray) node = node?.DeepClone();
        if (node is not JsonArray array)
            throw new FormatException($"expected a JSON array of Types, got {node?.GetValueKind() ?? JsonValueKind.Null}");
        var result = new TypeNode[array.Count];
        for (int i = 0; i < result.Length; i++) result[i] = Read(array[i]);
        return result;
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
                if (f.Classifier is { } source)
                    o["classifier"] = new JsonObject {
                        ["packageName"] = source.PackageName,
                        ["ownerPath"] = new JsonArray(source.OwnerPath.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                        ["name"] = source.Name,
                    };
                return o;
            }
            case Tv v:
                return new JsonObject { ["t"] = "tv", ["scope"] = v.Scope, ["i"] = v.I };
            case Star:
                return new JsonObject { ["t"] = "star" };
            case Projection p:
                if (p.Variance is not ("in" or "out"))
                    throw new ArgumentException($"projection variance must be `in` or `out`, got `{p.Variance}`");
                return new JsonObject { ["t"] = "projection", ["variance"] = p.Variance, ["of"] = Write(p.Of) };
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
            case FnPtr pointer:
                if (pointer.CallingConvention is < 0 or > 255 || pointer.GenericArity < 0
                    || pointer.RequiredParams < 0 || pointer.RequiredParams > pointer.Params.Length)
                    throw new ArgumentException("Function-pointer header, arity or required parameter count is malformed");
                return new JsonObject {
                    ["t"] = "fnPtr", ["cc"] = pointer.CallingConvention,
                    ["genericArity"] = pointer.GenericArity, ["requiredParams"] = pointer.RequiredParams,
                    ["ret"] = Write(pointer.Ret), ["params"] = WriteArray(pointer.Params),
                };
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

    /// <summary>Compact canonical JSON string of a type, sharing kotc's logical type contract.</summary>
    public static string ToJson(TypeNode t) =>
        TypeToJson(t);

    /// <summary>Parse a canonical type JSON string back into a <see cref="TypeNode"/>.</summary>
    public static TypeNode Parse(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
        if (!reader.Read()) throw new FormatException();
        var result = ReadJson(ref reader);
        if (reader.Read()) throw new FormatException();
        return result;
    }

    private static string TypeToJson(TypeNode type)
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = BirJson.MaxDepth }))
            WriteJson(writer, type);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}

/// <summary>
/// Carrier codec entry points (spec §0). Typed binary domains supply both JSON and MessagePack
/// callbacks through BirCarrier.MessagePack.cs; domains still migrating use the JSON-only overloads here.
/// The declared codec selects transport explicitly, without content sniffing or fallback.
/// </summary>
public static partial class BirCarrier
{
    public const string JsonV1 = "bir-json/1";

    public delegate T JsonBodyReader<T>(ref Utf8JsonReader reader);

    public static byte[] EncodeBody<T>(string version, T body, Action<Utf8JsonWriter, T> write)
    {
        if (version != JsonV1) throw new NotSupportedException($"unsupported carrier codec `{version}`");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = BirJson.MaxDepth }))
            write(writer, body);
        return output.ToArray();
    }

    public static T DecodeBody<T>(string version, byte[] content, JsonBodyReader<T> read)
    {
        if (version != JsonV1) throw new NotSupportedException($"unsupported carrier codec `{version}`");
        var reader = new Utf8JsonReader(content, new JsonReaderOptions {
            MaxDepth = BirJson.DocOptions.MaxDepth, CommentHandling = BirJson.DocOptions.CommentHandling,
            AllowTrailingCommas = BirJson.DocOptions.AllowTrailingCommas,
        });
        if (!reader.Read()) throw new FormatException();
        var body = read(ref reader);
        if (reader.Read()) throw new FormatException();
        return body;
    }

    public static byte[] EncodeBody(string version, JsonNode body)
    {
        return version switch
        {
            JsonV1 => Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions { WriteIndented = false })),
            _ => throw new NotSupportedException($"unsupported carrier codec `{version}`"),
        };
    }

    public static JsonNode DecodeBody(string version, byte[] content)
    {
        return version switch
        {
            JsonV1 => JsonNode.Parse(Encoding.UTF8.GetString(content), documentOptions: BirJson.DocOptions)
                      ?? throw new FormatException("carrier body decoded to a null JSON node"),
            _ => throw new NotSupportedException($"unsupported carrier codec `{version}`"),
        };
    }
}

/// <summary>
/// Round-trip self-test of the shared TypeNode contract. Proves Read(Write(node)) == node for every
/// variant and that both directions agree with the C# fixtures and schema property coverage. Called by
/// a throwaway harness during verification; NOT a Main (the shared file must not own an entry point).
/// </summary>
public static class TypeNodeSelfTest
{
    public static void Run()
    {
        // Canonical C# fixtures (required fields first, optional fields last). Kotlin tests independently
        // cover its semantic vocabulary; both encoders must cover their schema-owned properties.
        var cases = new (TypeNode node, string json)[]
        {
            (new TypeNode.Fqn("kotlin.Int"),
                "{\"t\":\"fqn\",\"name\":\"kotlin.Int\"}"),
            (new TypeNode.Fqn("example.Outer.Nested") {
                    Classifier = new TypeNode.SourceClassifier("example", ["Outer"], "Nested") },
                "{\"t\":\"fqn\",\"name\":\"example.Outer.Nested\",\"classifier\":{\"packageName\":\"example\",\"ownerPath\":[\"Outer\"],\"name\":\"Nested\"}}"),
            (new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { new TypeNode.Fqn("kotlin.Int") }),
                "{\"t\":\"fqn\",\"name\":\"kotlin.collections.List\",\"args\":[{\"t\":\"fqn\",\"name\":\"kotlin.Int\"}]}"),
            (new TypeNode.Fqn("Bounded", new TypeNode[] { new TypeNode.Star() }),
                "{\"t\":\"fqn\",\"name\":\"Bounded\",\"args\":[{\"t\":\"star\"}]}"),
            (new TypeNode.Fqn("Producer", new TypeNode[] {
                    new TypeNode.Projection("out", new TypeNode.Fqn("kotlin.String")) }),
                "{\"t\":\"fqn\",\"name\":\"Producer\",\"args\":[{\"t\":\"projection\",\"variance\":\"out\",\"of\":{\"t\":\"fqn\",\"name\":\"kotlin.String\"}}]}"),
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
            (new TypeNode.FnPtr(0, 0, 1, new TypeNode.Tv("method", 0), [new TypeNode.Tv("type", 0)]),
                """{"t":"fnPtr","cc":0,"genericArity":0,"requiredParams":1,"ret":{"t":"tv","scope":"method","i":0},"params":[{"t":"tv","scope":"type","i":0}]}"""),
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
            (new TypeNode.Oblivious(new TypeNode.Fqn("System.String")),
                """{"t":"oblivious","of":{"t":"fqn","name":"System.String"}}"""),
            (new TypeNode.Fn(true,
                    new TypeNode.Oblivious(new TypeNode.Fqn("R") {
                        Classifier = new TypeNode.SourceClassifier("", [], "R") }),
                    [new TypeNode.Projection("in", new TypeNode.Star())],
                    new TypeNode.Nullable(new TypeNode.Tv("method", 1)), "System.Func",
                    [TypeNode.Array.General(new TypeNode.Ptr(new TypeNode.Fqn("C")), TypeNode.MaxArrayRank)]),
                """{"t":"fn","suspend":true,"ret":{"t":"oblivious","of":{"t":"fqn","name":"R","classifier":{"packageName":"","ownerPath":[],"name":"R"}}},"params":[{"t":"projection","variance":"in","of":{"t":"star"}}],"recv":{"t":"nullable","of":{"t":"tv","scope":"method","i":1}},"clr":"System.Func","ctx":[{"t":"array","elem":{"t":"ptr","of":{"t":"fqn","name":"C"}},"rank":32}]}"""),
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
            if (parsed != node || TypeNode.ToJson(parsed) != json)
                throw new Exception($"[C# TypeNode] Read mismatch for {json}: got {parsed}");
            // Read(Write(node)) == node (the core round-trip property).
            if (TypeNode.Parse(TypeNode.ToJson(node)) != node)
                throw new Exception($"[C# TypeNode] round-trip mismatch for {json}");
            CheckReaders(TypeNode.Write(node), node);
            CheckReaders(JsonNode.Parse(json), node);
            n++;
        }
        CheckSchemaProperties(all.Select(sample => TypeNode.Write(sample.node)));
        CheckSchemaProperties(all.Select(sample => JsonNode.Parse(TypeNode.ToJson(sample.node))!));

        // Required enum values must satisfy the current schema when encoded.
        foreach (TypeNode malformed in new TypeNode[] {
            new TypeNode.Projection(null!, new TypeNode.Star()), new TypeNode.Tv(null!, 0),
        })
        {
            try { TypeNode.ToJson(malformed); }
            catch (ArgumentException) { continue; }
            throw new Exception("[C# TypeNode] writer emitted a null required enum");
        }

        // Object order is not part of type identity, including nested values and classifier metadata.
        var reordered = TypeNode.Parse("""
            {"args":[{"i":2,"scope":"method","t":"tv"}],
             "classifier":{"name":"Inner","ownerPath":["Outer"],"packageName":"demo"},
             "name":"demo.Outer.Inner","t":"fqn"}
            """);
        var ordered = new TypeNode.Fqn("demo.Outer.Inner", [new TypeNode.Tv("method", 2)]) {
            Classifier = new TypeNode.SourceClassifier("demo", ["Outer"], "Inner") };
        if (TypeNode.ToJson(reordered) != TypeNode.ToJson(ordered))
            throw new Exception("[C# TypeNode] streaming read depends on object property order");
        var escaped = new TypeNode.Fqn("Q\"\\\n\r\t\b\f\u0001κ😀");
        if (TypeNode.ToJson(escaped) != TypeNode.Write(escaped).ToJsonString()
            || TypeNode.Parse(TypeNode.ToJson(escaped)) != escaped)
            throw new Exception("[C# TypeNode] streaming string escaping mismatch");

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
        CheckNodeReading();
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

    private static void CheckSchemaProperties(IEnumerable<JsonNode> samples)
    {
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Remember(string tag, JsonObject obj)
        {
            if (!seen.TryGetValue(tag, out var names)) seen[tag] = names = new(StringComparer.Ordinal);
            names.UnionWith(obj.Select(pair => pair.Key));
        }
        void Walk(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["t"] is JsonValue tag) Remember(tag.GetValue<string>(), obj);
                if (obj["classifier"] is JsonObject classifier) Remember("SourceClassifier", classifier);
                foreach (var pair in obj) Walk(pair.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Walk(child);
        }
        foreach (var sample in samples) Walk(sample);
        foreach (var (tag, expected) in TypeNodeWireContract.Properties)
            if (!seen.TryGetValue(tag, out var actual) || !actual.SetEquals(expected))
                throw new Exception($"[C# TypeNode] encoded schema properties are not covered for {tag}");
    }

    private static void CheckReaders(JsonNode? node, TypeNode? expected = null)
    {
        string json = node?.ToJsonString() ?? "null";
        var elementRead = TypeNode.Parse(json);
        var nodeRead = TypeNode.Read(node);
        // Fqn equality deliberately excludes classifier metadata; compare its serialized form too,
        // including classifiers nested inside arguments, wrappers and function slots.
        if (elementRead != nodeRead || TypeNode.ToJson(elementRead) != TypeNode.ToJson(nodeRead)
            || expected is not null && (nodeRead != expected || TypeNode.ToJson(nodeRead) != TypeNode.ToJson(expected)))
            throw new Exception($"[C# TypeNode] JsonNode/JsonElement mismatch for {json}");
    }

    private static void CheckNodeReading()
    {
        foreach (string json in new[] {
            """{"t":"fqn","name":"G","args":[],"classifier":{"packageName":"p","ownerPath":["Outer","Inner"],"name":"G"}}""",
            """{"t":"fn","suspend":false,"ret":{"t":"star"},"params":[],"clr":null,"ctx":[]}""",
        }) CheckReaders(JsonNode.Parse(json));

        // JsonValue<T> keeps T, whereas parsed JsonValues use JsonElement. JSON's int32 contract
        // depends on the serialized number, not whether its in-memory CLR type happens to be int.
        foreach (JsonNode? number in new JsonNode?[] {
            JsonValue.Create((byte)2), JsonValue.Create((short)2), JsonValue.Create(2L),
            JsonValue.Create(2U), JsonValue.Create(2UL), JsonValue.Create(2f), JsonValue.Create(2d),
            JsonValue.Create(2m), JsonValue.Create(JsonDocument.Parse("2").RootElement.Clone()),
        })
        {
            CheckReaders(new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = number?.DeepClone() });
            CheckReaders(new JsonObject { ["t"] = "array", ["elem"] = TypeNode.Write(new TypeNode.Star()),
                ["rank"] = number?.DeepClone() });
        }
        CheckReaders(new JsonObject { ["t"] = "fqn", ["name"] = JsonValue.Create('T') });
        foreach (string name in new[] { "\uD800", "\uDC00", "a\uD800b", "\uD83D\uDE00" })
            CheckReaders(new JsonObject { ["t"] = "fqn", ["name"] = name });

        // Customized values retain their serializer's contract, not just their underlying CLR value.
        var numberOptions = new JsonSerializerOptions {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.WriteAsString,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        var numberTypeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<int>)numberOptions.GetTypeInfo(typeof(int));
        var stringRank = new JsonObject { ["t"] = "array", ["elem"] = TypeNode.Write(new TypeNode.Star()),
            ["rank"] = JsonValue.Create(2, numberTypeInfo) };
        Refuse(() => TypeNode.Parse(stringRank.ToJsonString()));
        Refuse(() => TypeNode.Read(stringRank));
        CheckReaders(JsonValue.Create<object>(new { t = "star" }));
        CheckReaders(new JsonObject { ["t"] = "nullable", ["of"] = JsonValue.Create<object>(new { t = "star" }) });
        CheckReaders(new JsonObject { ["t"] = "fqn", ["name"] = "G",
            ["args"] = JsonValue.Create<object>(new[] { new { t = "star" } }),
            ["classifier"] = JsonValue.Create<object>(new { packageName = "p", ownerPath = new[] { "O" }, name = "G" }) });
        var classifierOwners = TypeNode.Write(new TypeNode.Fqn("G") {
            Classifier = new TypeNode.SourceClassifier("p", [], "G") });
        classifierOwners["classifier"]!["ownerPath"] = JsonValue.Create<object>(new[] { "O" });
        CheckReaders(classifierOwners);

        var insensitiveOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        CheckReaders(new JsonObject(insensitiveOptions) { ["t"] = "array",
            ["elem"] = TypeNode.Write(new TypeNode.Star()), ["RANK"] = 2 });
        CheckReaders(new JsonObject(insensitiveOptions) { ["t"] = "fqn", ["name"] = "G", ["ARGS"] = 1 });
        var wrongCase = new JsonObject(insensitiveOptions) { ["t"] = "fqn", ["Name"] = "G" };
        Refuse(() => TypeNode.Parse(wrongCase.ToJsonString()));
        Refuse(() => TypeNode.Read(wrongCase));

        foreach (bool parsed in new[] { false, true })
        {
            var original = new TypeNode.Fqn("Before", [new TypeNode.Tv("method", 0)]) {
                Classifier = new TypeNode.SourceClassifier("p", ["Outer"], "Before") };
            var mutable = parsed ? JsonNode.Parse(TypeNode.ToJson(original)) ?? throw new Exception("null type")
                : TypeNode.Write(original);
            var before = TypeNode.Read(mutable);
            mutable["name"] = "After";
            mutable["args"]![0]!["i"] = 3L;
            mutable["classifier"]!["name"] = "After";
            mutable["classifier"]!["ownerPath"]![0] = "Changed";
            CheckReaders(mutable, new TypeNode.Fqn("After", [new TypeNode.Tv("method", 3)]) {
                Classifier = new TypeNode.SourceClassifier("p", ["Changed"], "After") });
            if (TypeNode.ToJson(before) != TypeNode.ToJson(original))
                throw new Exception("[C# TypeNode] mutation changed an already-decoded type");
        }

        // Current structured vocabulary only: absent required properties, wrong JSON kinds, invalid
        // projection variance and unrepresentable ranks. Do not freeze rejection of legacy formats.
        var invalid = new List<JsonNode?>();
        foreach (TypeNode sample in new TypeNode[] {
            new TypeNode.Fqn("T"), new TypeNode.Tv("type", 0), new TypeNode.Star(),
            new TypeNode.Projection("out", new TypeNode.Star()),
            new TypeNode.Fn(false, new TypeNode.Star(), []), new TypeNode.Nullable(new TypeNode.Star()),
            new TypeNode.Oblivious(new TypeNode.Star()), new TypeNode.Array(new TypeNode.Star()),
            new TypeNode.ByRef(new TypeNode.Star()), new TypeNode.Ptr(new TypeNode.Star()),
            new TypeNode.Mod(true, new TypeNode.Star(), new TypeNode.Star()),
        })
        {
            var written = (JsonObject)TypeNode.Write(sample);
            foreach (var property in written)
            {
                var missing = (JsonObject)written.DeepClone();
                missing.Remove(property.Key);
                invalid.Add(missing);
                var wrongKind = (JsonObject)written.DeepClone();
                wrongKind[property.Key] = new JsonObject();
                invalid.Add(wrongKind);
                var nullValue = (JsonObject)written.DeepClone();
                nullValue[property.Key] = null;
                invalid.Add(nullValue);
            }
        }
        foreach (string json in new[] {
            """{"t":"projection","variance":"invalid","of":{"t":"star"}}""",
            """{"t":"fqn","name":"T","args":null}""",
            """{"t":"fqn","name":"T","args":[null]}""",
            """{"t":"fqn","name":"T","classifier":{}}""",
            """{"t":"fqn","name":"T","classifier":{"packageName":"p","ownerPath":[1],"name":"T"}}""",
            """{"t":"fn","suspend":false,"ret":{"t":"star"},"params":[],"recv":null}""",
            """{"t":"fn","suspend":false,"ret":{"t":"star"},"params":[],"ctx":null}""",
            """{"t":"fn","suspend":false,"ret":{"t":"star"},"params":[],"clr":1}""",
        }) invalid.Add(JsonNode.Parse(json));
        foreach (JsonNode? number in new JsonNode?[] {
            null, JsonValue.Create(0), JsonValue.Create(-1), JsonValue.Create(TypeNode.MaxArrayRank + 1),
            JsonValue.Create(long.MaxValue), JsonValue.Create(1.5), JsonValue.Create(2.0m), JsonValue.Create("2"),
        }) invalid.Add(new JsonObject { ["t"] = "array", ["elem"] = TypeNode.Write(new TypeNode.Star()), ["rank"] = number });
        foreach (var node in invalid)
        {
            Refuse(() => TypeNode.Parse(node?.ToJsonString() ?? "null"));
            Refuse(() => TypeNode.Read(node));
            Refuse(() => TypeNode.Read(JsonNode.Parse(node?.ToJsonString() ?? "null")));
        }
    }

    private static void Refuse(Action read)
    {
        try { read(); }
        catch (Exception e) when (e is FormatException or KeyNotFoundException or InvalidOperationException) { return; }
        throw new Exception("[C# TypeNode] accepted malformed current type");
    }
}
