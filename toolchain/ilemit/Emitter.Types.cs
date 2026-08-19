// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Type-spec mapping: BIR type tokens/TypeNode -> System.Type (MapType, ClrRef, generic construction).
sealed partial class Emitter
{
    // A target-MLC generic definition cannot materialize a runtime Reflection.Emit generic parameter through its
    // MakeGenericType implementation. The CLR signature is nevertheless valid; MakeGenericSignatureType is the
    // framework-provided representation for exactly that cross-reflection signature shape. A locally emitted open
    // TypeBuilder must keep its own TypeBuilderInstantiation so TypeBuilder.GetMethod/GetConstructor continue to work.
    static Type ConstructedType(Type definition, params Type[] arguments) =>
        definition is not TypeBuilder && arguments.Any(ContainsTypeBuilder)
            ? Type.MakeGenericSignatureType(definition, arguments)
            : definition.MakeGenericType(arguments);

    // The bare NAME a type slot carries, for a name-keyed opcode switch (const/conv).
    static string SlotName(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fqn f
            ? f.Name
            : null;

    // A primitive type slot may now arrive as the @ClrTypeAlias BCL name ("System.Int32") rather than the CLR
    // shorthand ("int"): bir2cir routes primitives through the ref.dll alias index (the redundant hardcoded
    // KotlinToClr shadow was deleted, #55), so a primitive lowers to its `@ClrTypeAlias("System.Int32")` form.
    // Normalize the alias spelling back to ilemit's opcode-alphabet shorthand so the name-keyed opcode switches
    // (EmitConst/EmitConv/ConstArgValue) key uniformly. Signedness is preserved from the alias: System.SByte is
    // SIGNED (Kotlin Byte -> "sbyte"), System.Byte is UNSIGNED (Kotlin UByte -> "byte") — matching #53/#54.
    static string PrimShorthandName(string t) => t switch
    {
        "System.Int32" => "int", "System.Int64" => "long", "System.Int16" => "short", "System.SByte" => "sbyte",
        "System.Double" => "double", "System.Single" => "float", "System.Boolean" => "bool", "System.Char" => "char",
        "System.String" => "string", "System.Object" => "object", "System.Void" => "void",
        "System.UInt32" => "uint", "System.UInt64" => "ulong", "System.Byte" => "byte", "System.UInt16" => "ushort",
        _ => t,
    };

    // BIR `clrg:<openName>[<arg1>,<arg2>,...]` -> a constructed generic .NET type. Args split at bracket-depth 0
    // so nested generics (List[ValueTuple[int,string]]) parse correctly.
    // Resolve a .NET type reference that may be a plain name (ResolveType), a generic `clrg:Open[args]`,
    // or a func/closed encoding (MapType). Used by newClr/clrPropGet so they accept generic types (System.Lazy<T>).
    // A clr* owner/type slot is a structured Type node authored by bir2cir.
    Type ClrRef(JsonElement e) => MapType(DotKt.Bir.TypeNode.Read(e));

    // A bare type-IDENTITY string (no legacy grammar prefix — those are retired, #48): a CLR-shorthand primitive routes
    // through MapType (which owns the shorthand switch — `argTypes` may synthesize e.g. "string" so the ctor-overload
    // lookup binds StringBuilder(String) not StringBuilder(Int32)); every other bare FQN resolves by reflection.
    Type ClrRef(string s) =>
        PrimShorthand.Contains(s) ? MapType(s) :
        ResolveType(s);

    static readonly HashSet<string> PrimShorthand = new(StringComparer.Ordinal)
    { "void", "object", "string", "int", "long", "short", "sbyte", "double", "float", "bool", "char", "uint", "ulong", "ushort", "byte" };

    // Generic arguments must already be physical value types in CIR. `System.Void` is not a legal CLR generic
    // argument; converting it to object here would reconstruct the Kotlin Unit/Nothing representation.
    Type MapArg(string t) => RequireGenericArgument(MapType(t), t);

    Type RequireGenericArgument(Type type, object source)
    {
        if (type == Bcl("System.Void"))
            throw new NotSupportedException($"invalid CIR generic argument `System.Void` from {source}");
        return type;
    }

    Type GenericType(string spec)
    {
        var br = spec.IndexOf('[');
        var open = spec.Substring(0, br);
        var inner = spec.Substring(br + 1, spec.Length - br - 2);
        var args = SplitTopLevel(inner).Select(MapArg).ToArray();
        var openGen = ResolveType(open + "`" + args.Length);
        return ConstructedType(openGen, args);
    }

    static List<string> SplitTopLevel(string s)
    {
        var res = new List<string>(); int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']') depth--;
            else if (s[i] == ',' && depth == 0) { res.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        if (s.Length > 0) res.Add(s.Substring(start));
        return res;
    }

    Type MapType(JsonElement e) => MapType(DotKt.Bir.TypeNode.Read(e));

    Type MapType(DotKt.Bir.TypeNode t) => t switch
    {
        DotKt.Bir.TypeNode.ByRef b => MapType(b.Of).MakeByRefType(),
        DotKt.Bir.TypeNode.Array { SzArray: true, Rank: 1 } a => MapType(a.Elem).MakeArrayType(),
        DotKt.Bir.TypeNode.Array a => MapType(a.Elem).MakeArrayType(a.Rank),
        DotKt.Bir.TypeNode.Ptr p => MapType(p.Of).MakePointerType(),
        DotKt.Bir.TypeNode.Nullable n => MapNullable(n),
        DotKt.Bir.TypeNode.Fn fn => FuncType(fn),
        DotKt.Bir.TypeNode.Tv tv => ResolveTv(tv),
        DotKt.Bir.TypeNode.Fqn { Args: null } f => MapType(f.Name),   // reuse the shorthand / bare-FQN resolver
        DotKt.Bir.TypeNode.Fqn f => ConstructGeneric(f.Name, f.Args),
        _ => throw new NotSupportedException($"unencodable CIR type node `{t.GetType().Name}`"),
    };

    // #37/#48: nullability realizes value-vs-reference HERE (MapType resolves the inner type, so it's the natural
    // split point). VALUE-type nullability is STRUCTURAL -> `System.Nullable<T>`. REFERENCE-type nullability is
    // METADATA-only -> the bare reference type (the IL type of `String?` IS `String`; its nullability rides an NRT
    // byte at DECL positions, computed by bir2cir). bir2cir strips + byte-walks reference nullables at decl positions;
    // any `{t:nullable,of:<reference>}` that still reaches here is a USAGE position (an owner/nested type-arg, e.g.
    // `Continuation<Any?>`) where nullability is compile-time-only and carries no byte — so it simply resolves bare.
    Type MapNullable(DotKt.Bir.TypeNode.Nullable n)
    {
        var inner = MapType(n.Of);
        return IsValueType(inner) ? ConstructedType(Bcl("System.Nullable`1"), inner) : inner;
    }

    // A constructed generic from a structured Fqn(name, args): an emitted open type -> MakeGenericType, else a
    // referenced .NET generic by arity-suffixed FQN. Every argument must already be CLR-legal in CIR.
    Type ConstructGeneric(string name, DotKt.Bir.TypeNode[] args)
    {
        var mapped = args.Select(a => RequireGenericArgument(MapType(a), a)).ToArray();
        if (_types.TryGetValue(name, out var oti)) return ConstructedType(oti.AsType, mapped);
        // A NESTED generic whose arity backtick rides an OUTER type already carries a backtick in `name` (e.g. the #3
        // generic ConfigureAwait(false) awaiter `System...ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter` — arity `1 is
        // on the OUTER ConfiguredTaskAwaitable, the nested awaiter has none). Appending a SECOND arity suffix here yields
        // `...ConfiguredTaskAwaiter`1`, which ResolveType can't find — the name is already arity-complete, use it verbatim.
        var open = name.Contains('`') ? ResolveType(name) : ResolveType(name + "`" + mapped.Length);
        return ConstructedType(open, mapped);
    }

    // A `tv` (scope + flattened index) -> the CLR generic-parameter builder: scope "method" -> the method's own params
    // (`!!i`, GenericMethodParameter), scope "type" -> the enclosing type's flattened params (`!i`, GenericTypeParameter).
    Type ResolveTv(DotKt.Bir.TypeNode.Tv tv)
    {
        var pool = tv.Scope == "method" ? _curMethodParams : _curTypeParams;
        if (pool != null)
            foreach (var g in pool.Values)
                if (g.GenericParameterPosition == tv.I) return g;
        throw new NotSupportedException(
            $"unresolved CIR {tv.Scope} generic parameter at index {tv.I}; "
            + "bir2cir must provide the exact CLR generic-parameter frame");
    }

    // Structured CIR function type -> the exact CLR delegate family selected by bir2cir.
    Type FuncType(DotKt.Bir.TypeNode.Fn fn)
    {
        // DelegateParams prepends an extension receiver (`P.() -> R` = `Func<P,R>`/`KAction`1[P]`) so a restored
        // receiver-lambda param type builds the SAME CLR delegate as the flat lambda-value closure bound to it.
        var args = fn.DelegateParams.Select(MapType).ToArray();
        var ret = MapType(fn.Ret);
        return BuildFuncType(args, ret, fn.Clr
            ?? throw new NotSupportedException("CIR function type is missing bir2cir-resolved `clr` delegate family"));
    }

    // Realize bir2cir's nominal ABI decision 1:1. TypeBuilder-involving delegate instantiations are handled by
    // DelegateCtor/InvokeOf and never alter signature identity. Family validity and arity range were decided before CIR.
    Type BuildFuncType(Type[] args, Type ret, string clr)
    {
        return clr switch
        {
            "System.Action" when ret == Bcl("System.Void") && args.Length == 0 => Bcl("System.Action"),
            "System.Action" when ret == Bcl("System.Void") =>
                ConstructedType(ResolveType("System.Action`" + args.Length), args),
            "System.Func" when ret != Bcl("System.Void") =>
                ConstructedType(ResolveType("System.Func`" + (args.Length + 1)), args.Append(ret).ToArray()),
            "DotKt.Runtime.CompilerServices.KAction" when ret == Bcl("System.Void") =>
                CanonicalActionType(args),
            "DotKt.Runtime.CompilerServices.KFunc" when ret != Bcl("System.Void") =>
                CanonicalFuncType(args, ret),
            _ => throw new NotSupportedException(
                $"invalid CIR delegate family `{clr}` for arity {args.Length}, return {ret}")
        };
    }

    // A generic type parameter resolved by NAME in context (method params shadow the enclosing type's). The structured
    // TypeNode.Tv path uses positional `ResolveTv`; this name lookup serves the few places that hold only the CLR
    // builder's generic-param NAME (a closure's own type args — ResolveClosure).
    Type GenericParamByName(string gpName)
    {
        if (_curMethodParams != null && _curMethodParams.TryGetValue(gpName, out var mgp)) return mgp;
        if (_curTypeParams != null && _curTypeParams.TryGetValue(gpName, out var tgp)) return tgp;
        throw new NotSupportedException("unresolved generic type parameter " + gpName);
    }

    // A type NAME slot that is NOT a structured node — a bare FQN / CLR-shorthand IDENTITY (an owner-FQN island, a
    // primitive shorthand). Every value type travels as a structured `{t:…}` node (MapType(TypeNode)); this string
    // resolver handles ONLY the bare-identity slots. `dotkt$stackptr` is the one synthetic pseudo-type kept — a canonical
    // compiler-internal identity in the `dotkt$` synthetic namespace (#48), NOT a Kotlin/CLR type.
    Type MapType(string t)
    {
        if (t == "dotkt$stackptr") return Bcl("System.Byte").MakePointerType();   // a localloc'd stack buffer pointer (unverifiable)
        return t switch
        {
            "void" => Bcl("System.Void"), "int" => Bcl("System.Int32"), "long" => Bcl("System.Int64"),
            "double" => Bcl("System.Double"), "float" => Bcl("System.Single"), "bool" => Bcl("System.Boolean"),
            "char" => Bcl("System.Char"), "string" => Bcl("System.String"), "object" => Bcl("System.Object"),
            "uint" => Bcl("System.UInt32"), "ulong" => Bcl("System.UInt64"), "byte" => Bcl("System.Byte"), "ushort" => Bcl("System.UInt16"),
            // .NET-aligned 8-bit tokens (#54): token "sbyte" is SIGNED (kotlin.Byte, -128..127); token "byte" is
            // UNSIGNED (kotlin.UByte, System.Byte, 0..255) — matching int/short/long naming.
            "short" => Bcl("System.Int16"), "sbyte" => Bcl("System.SByte"),
            // A bare FQN identity (kotc's pure-FQN output — NO `@`/`clr:` marker): an in-assembly emitted type wins;
            // every other identity must resolve exactly from the declared reference universe.
            // A bare constructed-generic `Name[args]` whose open name isn't emitted here (e.g. the `ownerType` of a
            // referenced `kotlin.Result[int]` member call) resolves as a referenced generic (GenericType arity-suffixes).
            // A dot-LESS name not emitted in THIS assembly but present in a REFERENCED (--ref, LoadFrom'd) assembly is a
            // real external type — a `dotkt$*` canonical synthetic (`dotkt$CharSequence`) OR a root-package library
            // class (`Vec`/`Lib`/`Pt`, no namespace). Resolve it by reflection, don't fall to object. Before the TYPE flip
            // these rode the `@dotkt$X`/`@Name` emitted-type-hint branch; kotc/bir2cir now emit the bare FQN, so a
            // dot-less name that ResolvesExternally must route to ResolveType here (mirrors the externalSynthIface path).
            _ => TryMapEmittedType(t) ?? ((t != null && t.Contains('[')) ? GenericType(t) : ResolveType(t)),
        };
    }

    // Resolve a bare type spec (no `@`/`clr:`/shorthand prefix) against THIS assembly's emitted types (`_types`).
    // Handles the plain `Name` and the constructed-generic `Name[arg,...]` forms (the `_types` key is the open name
    // WITHOUT arity, so the `[...]` suffix is stripped to look it up). Returns null when the name is not emitted here
    // (the caller then falls back to reflection over referenced assemblies).
    Type TryMapEmittedType(string spec)
    {
        if (spec == null) return null;
        var br = spec.IndexOf('[');
        if (br < 0) return _types.TryGetValue(spec, out var ti) ? ti.AsType : null;
        var open = spec.Substring(0, br);
        if (!_types.TryGetValue(open, out var oti)) return null;
        var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapArg).ToArray();
        return ConstructedType(oti.AsType, args);
    }

}
