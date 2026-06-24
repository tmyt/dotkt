// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// The `[Kotlin*]` round-trip metadata attributes (from DotKt.Runtime) and the BIR `attr`-node constant decoding.
sealed partial class Emitter
{
    // Build a .NET custom attribute from a BIR `attr` node (a user annotation): the synthesized `: System.Attribute`
    // class's ctor + compile-time-constant args.
    // DotKt metadata attribute types (from DotKt.Runtime, --ref'd). Null when not referenced -> stamping is skipped.
    static bool _kAttrsResolved;
    static Type _kFuncAttr, _kFuncFlags, _kFileAttr, _kInlineAttr, _kNullableAttr, _kReadOnlyAttr, _kNsProjAttr;
    static void ResolveKotlinAttrs()
    {
        if (_kAttrsResolved) return;
        _kAttrsResolved = true;
        _kFuncAttr = TryResolveType("DotKt.Metadata.KotlinFunctionAttribute");
        _kFuncFlags = TryResolveType("DotKt.Metadata.KotlinFunctionFlags");
        _kFileAttr = TryResolveType("DotKt.Metadata.KotlinFileClassAttribute");
        _kInlineAttr = TryResolveType("DotKt.Metadata.KotlinInlineAttribute");
        _kNullableAttr = TryResolveType("DotKt.Metadata.KotlinNullableAttribute");
        _kReadOnlyAttr = TryResolveType("DotKt.Metadata.KotlinReadOnlyAttribute");
        _kNsProjAttr = TryResolveType("DotKt.Metadata.DotKtNamespaceProjectionAttribute");
    }

    // [KotlinNullable(mask)] — the Kotlin nullability of the signature (bit 0 = return, bit i+1 = param i).
    static void ApplyKotlinNullable(MethodBuilder mb, uint mask)
    {
        ResolveKotlinAttrs();
        var ctor = _kNullableAttr?.GetConstructor(new[] { typeof(uint) });
        if (ctor == null) return;
        mb.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { mask }));
    }

    // [KotlinReadOnly] — a public backing field whose Kotlin property isn't publicly settable (restore as `val`).
    static void ApplyKotlinReadOnly(FieldBuilder fb)
    {
        ResolveKotlinAttrs();
        var ctor = _kReadOnlyAttr?.GetConstructor(Type.EmptyTypes);
        if (ctor == null) return;
        fb.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[0]));
    }

    // [KotlinInline(body)] — the inline+lambda fn's BIR body, for cross-module splicing.
    static void ApplyKotlinInline(MethodBuilder mb, string body)
    {
        ResolveKotlinAttrs();
        var ctor = _kInlineAttr?.GetConstructor(new[] { typeof(string) });
        if (ctor == null) return;
        mb.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { body }));
    }

    // [KotlinFunction(flags)] — Kotlin modifiers with no .NET analog (infix/operator/suspend), for Kotlin re-consumption.
    static void ApplyKotlinFunction(MethodBuilder mb, int flags)
    {
        ResolveKotlinAttrs();
        if (_kFuncAttr == null || _kFuncFlags == null) return;
        var ctor = _kFuncAttr.GetConstructor(new[] { _kFuncFlags });
        if (ctor == null) return;
        mb.SetCustomAttribute(new CustomAttributeBuilder(ctor, new[] { Enum.ToObject(_kFuncFlags, flags) }));
    }

    // [KotlinFileClass] — marks a `<File>Kt` facade so its statics restore as top-level Kotlin functions.
    static void ApplyKotlinFileClass(TypeBuilder tb)
    {
        ResolveKotlinAttrs();
        var ctor = _kFileAttr?.GetConstructor(Type.EmptyTypes);
        if (ctor == null) return;
        tb.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[0]));
    }

    CustomAttributeBuilder BuildCab(JsonElement a)
    {
        var attr = a.GetProperty("attr").GetString();
        var args = a.GetProperty("args").EnumerateArray().Select(ConstArgValue).ToArray();
        if (attr.StartsWith("clr:"))
        {
            // An imported .NET attribute (#54): bind its real constructor (resolved by the declared arg types,
            // falling back to arity) and apply it with the constant args.
            var at = ClrRef(attr);
            var argTypes = a.GetProperty("argTypes").EnumerateArray().Select(s => ClrRef(s.GetString())).ToArray();
            var nctor = at.GetConstructor(argTypes)
                        ?? at.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == args.Length);
            return new CustomAttributeBuilder(nctor, args);
        }
        var ti = _types[attr];
        var ctor = ti.Ctors.Count > 0 ? ti.Ctors[0] : ti.TB.DefineDefaultConstructor(MethodAttributes.Public);
        return new CustomAttributeBuilder(ctor, args);
    }

    static object ConstArgValue(JsonElement e)
    {
        // Annotation arguments are always compile-time constants (const nodes).
        if (!e.TryGetProperty("value", out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                return e.GetProperty("type").GetString() switch
                {
                    "long" => (object)v.GetInt64(),
                    "double" => v.GetDouble(),
                    "float" => (float)v.GetDouble(),
                    "short" => (short)v.GetInt32(),
                    "byte" => (sbyte)v.GetInt32(),
                    _ => v.GetInt32(),
                };
            default: return null;
        }
    }
}
