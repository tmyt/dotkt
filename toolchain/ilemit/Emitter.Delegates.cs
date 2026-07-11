// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Synthetic delegate types + Kotlin function-type (FunctionN/Action) resolution.
sealed partial class Emitter
{
    // The embedded round-trip attribute namespace (#71 S2: the attribute CLASSES are now ordinary CIR type decls
    // emitted by bir2cir; this const only names the synthetic-delegate metadata namespace below).
    const string CompilerServicesNs = "DotKt.Runtime.CompilerServices.";

    // ilemit AUTHORS its own synthetic high-arity delegate types; mark each [KotlinFunction(0)] (a plain function
    // type — no infix/operator/suspend) so facadegen restores it as a Kotlin function type. This is ilemit stamping
    // its OWN emitted member (analogous to StampCompilerGenerated), NOT round-trip generation over user code: the
    // attribute CLASS is the ordinary CIR-defined `KotlinFunctionAttribute` in `_types` (bir2cir emits it, #71 S2),
    // whose (int) ctor is resolved generically. Absent (a --no-stdlib or runtime build that emits no attr class) -> skip.
    void StampKotlinFunctionZero(TypeBuilder tb)
    {
        if (!_types.TryGetValue(CompilerServicesNs + "KotlinFunctionAttribute", out var ti)) return;
        EnsureCtorsDefined(ti);
        if (ti.Ctors.Count == 0) return;
        tb.SetCustomAttribute(new CustomAttributeBuilder(ti.Ctors[0], new object[] { 0 }));
    }

    readonly Dictionary<string, TypeBuilder> _syntheticDelegates = new();

    readonly Dictionary<TypeBuilder, ConstructorBuilder> _syntheticDelegateCtors = new();

    readonly Dictionary<TypeBuilder, MethodBuilder> _syntheticDelegateInvokes = new();

    ConstructorInfo DelegateCtor(Type ft)
    {
        var sig = new[] { typeof(object), typeof(IntPtr) };
        if (ft.IsGenericType && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateCtors.TryGetValue(dtb, out var dctor))
            return TypeBuilder.GetConstructor(ft, dctor);
        return (IsGenericInst(ft) && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            ? TypeBuilder.GetConstructor(ft, ft.GetGenericTypeDefinition().GetConstructor(sig))
            : ft.GetConstructor(sig);
    }

    // A generic INSTANTIATION test that survives the new Reflection.Emit (where a TypeBuilderInstantiation reports
    // IsGenericType=false): its generic-arg list is still populated.
    static bool IsGenericInst(Type t) => !t.IsGenericParameter && t.GetGenericArguments().Length > 0;

    // The delegate's `Invoke` method, bridged via TypeBuilder.GetMethod for a TypeBuilder-involving instantiation.
    MethodInfo InvokeOf(Type ft)
    {
        if (IsGenericInst(ft) && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateInvokes.TryGetValue(dtb, out var invoke))
            return TypeBuilder.GetMethod(ft, invoke);
        if (IsGenericInst(ft) && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            return TypeBuilder.GetMethod(ft, ft.GetGenericTypeDefinition().GetMethod("Invoke"));
        return ft.GetMethod("Invoke");
    }

    // The RETURN .NET type from a `func:<ret>:<args>` string — carried by the BIR, so we never reflect the
    // ReturnType of a TypeBuilder-baked Invoke (which is unreliable on an un-baked generic instantiation).
    // Structured funcType (a Fn node) or a legacy `func:` string -> the delegate's return type / mapped arg types.
    Type FuncRetType(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fn fn
            ? MapType(fn.Ret) : FuncRetType(e.GetString());

    List<Type> FuncArgTypes(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fn fn
            ? fn.Params.Select(MapType).ToList()
            : FuncArgSpecs(e.GetString()).Select(MapType).ToList();

    Type FuncRetType(string t)
    {
        var rest = t.Substring(5);
        var ret = rest.Substring(0, FuncRetEnd(rest));
        return ret == "void" ? typeof(void) : MapType(ret);
    }

    // The delegate's PARAMETER type specs from a `func:<ret>:<arg,arg,...>` funcType token (the `<ret>` may itself be a
    // bracketed/prefixed type whose own ':' is not the separator — split at the first depth-0 ':' after the ret prefix).
    // Empty for a nullary function type. Used to coerce delegateInvoke args to the Invoke param the JIT expects.
    List<string> FuncArgSpecs(string t)
    {
        var rest = t.Substring(5);
        var argsPart = rest.Substring(FuncRetEnd(rest) + 1);
        return argsPart.Length == 0 ? new List<string>() : SplitTopLevel(argsPart).ToList();
    }

    // BIR `func:<ret>:<arg1>,<arg2>,...` -> a System.Func<...> (ret != void) or System.Action<...>.
    Type FuncType(string t)
    {
        var rest = t.Substring(5);
        // RET:ARGS — but RET may itself be a prefixed/bracketed type whose own ':' (clrg:Task[int]) must NOT be
        // taken as the separator. Find the first ':' at bracket-depth 0 AFTER any leading type prefix.
        var colon = FuncRetEnd(rest);
        var ret = rest.Substring(0, colon);
        var argsPart = rest.Substring(colon + 1);
        var args = SplitTopLevel(argsPart).Select(MapType).ToArray();
        return BuildFuncType(args, ret == "void" ? typeof(void) : MapType(ret));
    }

    Type SyntheticFuncType(Type[] args, Type ret) =>
        SyntheticDelegateType("KFunc", args.Append(ret).ToArray(), returnsValue: true).MakeGenericType(args.Append(ret).ToArray());

    Type SyntheticActionType(Type[] args) =>
        SyntheticDelegateType("KAction", args, returnsValue: false).MakeGenericType(args);

    TypeBuilder SyntheticDelegateType(string baseName, Type[] genericArgs, bool returnsValue)
    {
        var arity = genericArgs.Length;
        var metadataName = CompilerServicesNs + baseName + "`" + arity;
        if (_syntheticDelegates.TryGetValue(metadataName, out var cached))
            return cached;

        var tb = _mod.DefineType(metadataName,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(MulticastDelegate));
        tb.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
        StampKotlinFunctionZero(tb);

        var names = Enumerable.Range(1, arity).Select(i => i == arity && returnsValue ? "TResult" : "T" + i).ToArray();
        var gps = tb.DefineGenericParameters(names);
        var invokeParams = returnsValue ? gps.Take(arity - 1).Cast<Type>().ToArray() : gps.Cast<Type>().ToArray();
        var invokeRet = returnsValue ? (Type)gps[^1] : typeof(void);

        var ctor = tb.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
            CallingConventions.Standard,
            new[] { typeof(object), typeof(IntPtr) });
        ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        var invoke = tb.DefineMethod(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            invokeRet,
            invokeParams);
        invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        _syntheticDelegates[metadataName] = tb;
        _syntheticDelegateCtors[tb] = ctor;
        _syntheticDelegateInvokes[tb] = invoke;
        return tb;
    }

    // Index of the ':' separating RET from ARGS in a `func:` BODY (the leading "func:" already stripped by the caller).
    // When the RET is itself a NESTED func — `(Int)->(()->Int)` encodes as body `func:kotlin.Int::kotlin.Int` — the
    // inner func's OWN ret/args colon sits at depth 0 and the old "skip one prefix, grab first ':'" split mis-parsed it
    // (ret=`func:kotlin.Int`, args=`:kotlin.Int`, leaving `:kotlin.Int` unresolvable). Recursively skip the whole inner
    // func in that case. Every OTHER ret shape (leaf / clrg:/array:/nullable: with its own bracket-protected or single
    // leading colon) keeps the prior single-prefix scan — scoped narrowly so only the genuine nested-func-ret changes.
    static int FuncRetEnd(string s)
    {
        if (s.StartsWith("func:", StringComparison.Ordinal) || s.StartsWith("sfunc:", StringComparison.Ordinal))
            return SkipTypeToken(s, 0);
        int start = 0;
        foreach (var pre in new[] { "clrg:", "clr:", "array:", "nullable:", "gp:", "byref:" })
            if (s.StartsWith(pre)) { start = pre.Length; break; }
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']') depth--;
            else if (s[i] == ':' && depth == 0) return i;
        }
        return s.Length;
    }

    // Advance past exactly ONE type token at `i`; return the index just after it (a top-level ':' / ',' / ']' / end).
    // A `func:` token recurses through its ret + its comma-list args (args present iff the next char begins a type);
    // a modifier prefix (array:/nullable:/byref:) recurses into its element; a clrg:/clr:/gp:/leaf token scans to the
    // next top-level delimiter with [] nesting protecting inner ':'/','. Pure structural parse — no type resolution.
    static int SkipTypeToken(string s, int i)
    {
        static bool At(string s, int i, string pre) => i + pre.Length <= s.Length && s.AsSpan(i, pre.Length).SequenceEqual(pre);
        foreach (var pre in new[] { "array:", "nullable:", "byref:" })
            if (At(s, i, pre)) return SkipTypeToken(s, i + pre.Length);
        if (At(s, i, "func:"))
        {
            i = SkipTypeToken(s, i + 5);                                    // ret
            if (i < s.Length && s[i] == ':') i++;                          // ret/args separator
            if (i < s.Length && s[i] != ':' && s[i] != ',' && s[i] != ']') // non-empty args -> comma-list
            {
                i = SkipTypeToken(s, i);
                while (i < s.Length && s[i] == ',') i = SkipTypeToken(s, i + 1);
            }
            return i;
        }
        foreach (var pre in new[] { "clrg:", "clr:", "gp:" })
            if (At(s, i, pre)) { i += pre.Length; break; }
        int depth = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '[') depth++;
            else if (c == ']') { if (depth == 0) break; depth--; }
            else if (depth == 0 && (c == ':' || c == ',')) break;
        }
        return i;
    }

}
