// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Operator/constant/array/concat expression emission (arithmetic, conversions, literals).
sealed partial class Emitter
{
    Type EmitConst(JsonElement e)
    {
        var t = PrimShorthandName(SlotName(e.GetProperty("type")));
        var v = e.GetProperty("value");
        switch (t)
        {
            case "string":
                if (v.ValueKind == JsonValueKind.Null) { _il.Emit(OpCodes.Ldnull); return Bcl("System.String"); }
                _il.Emit(OpCodes.Ldstr, v.GetString()); return Bcl("System.String");
            case "int": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return Bcl("System.Int32");
            case "long": _il.Emit(OpCodes.Ldc_I8, v.GetInt64()); return Bcl("System.Int64");
            // Unsigned consts carry the SIGNED bit-pattern (e.g. 4000000000u stored as -294967296); the same
            // ldc opcode loads the right bits, only the stack TYPE differs (so add/print are unsigned).
            case "uint": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return Bcl("System.UInt32");
            case "ulong": _il.Emit(OpCodes.Ldc_I8, v.GetInt64()); return Bcl("System.UInt64");
            case "byte": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return Bcl("System.Byte");
            case "ushort": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return Bcl("System.UInt16");
            // Signed Byte/Short (Kotlin Byte = sbyte token, Short = Int16). Without these a `const sbyte`/`const short`
            // fell to default -> Ldnull -> InvalidProgramException when passed to an sbyte/short parameter.
            case "sbyte": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return Bcl("System.SByte");
            case "short": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return Bcl("System.Int16");
            // NaN / ±Infinity are emitted as a JSON STRING (not a number token, which JSON forbids) — parse them back.
            case "double": _il.Emit(OpCodes.Ldc_R8, v.ValueKind == JsonValueKind.String ? double.Parse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture) : v.GetDouble()); return Bcl("System.Double");
            case "float": _il.Emit(OpCodes.Ldc_R4, v.ValueKind == JsonValueKind.String ? float.Parse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture) : v.GetSingle()); return Bcl("System.Single");
            case "bool": _il.Emit(v.GetBoolean() ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); return Bcl("System.Boolean");
            case "char": _il.Emit(OpCodes.Ldc_I4, (int)v.GetString()[0]); return Bcl("System.Char");
            default: _il.Emit(OpCodes.Ldnull); return Bcl("System.Object");
        }
    }

    // Push a .NET CONSTANT (literal field) value, inlined — mirrors how C# emits a `const` read. `ft` is the field's
    // declared type (its underlying type if it's an enum). Returns `ft` (the stack type).
    Type EmitLiteralValue(object cv, Type ft)
    {
        var ut = ft.IsEnum ? Enum.GetUnderlyingType(ft) : ft;
        if (cv == null) { _il.Emit(OpCodes.Ldnull); return ft; }
        if (ut == Bcl("System.String")) { _il.Emit(OpCodes.Ldstr, (string)cv); return ft; }
        if (ut == Bcl("System.Boolean")) { _il.Emit((bool)cv ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); return ft; }
        if (ut == Bcl("System.Single")) { _il.Emit(OpCodes.Ldc_R4, Convert.ToSingle(cv)); return ft; }
        if (ut == Bcl("System.Double")) { _il.Emit(OpCodes.Ldc_R8, Convert.ToDouble(cv)); return ft; }
        if (ut == Bcl("System.Int64") || ut == Bcl("System.UInt64")) { _il.Emit(OpCodes.Ldc_I8, unchecked((long)Convert.ToUInt64(cv))); return ft; }
        // char and every <=32-bit integer load via ldc.i4 (the bit pattern).
        if (ut == Bcl("System.Char")) { _il.Emit(OpCodes.Ldc_I4, (int)(char)cv); return ft; }
        if (ut == Bcl("System.UInt32")) { _il.Emit(OpCodes.Ldc_I4, unchecked((int)Convert.ToUInt32(cv))); return ft; }
        _il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(cv)); return ft;   // sbyte/byte/short/ushort/int
    }

    int NumRank(Type t) =>
        t == Bcl("System.Double") ? 5 : t == Bcl("System.Single") ? 4 :
        (t == Bcl("System.Int64") || t == Bcl("System.UInt64")) ? 3 :
        (t == Bcl("System.Int32") || t == Bcl("System.UInt32")) ? 2 :
        (t == Bcl("System.Int16") || t == Bcl("System.UInt16") || t == Bcl("System.Char")) ? 1 :
        (t == Bcl("System.Byte") || t == Bcl("System.SByte")) ? 0 : -1;

    // The common numeric type of two operands (the wider), or null if no coercion is needed / they're not numeric.
    Type NumericCommon(Type a, Type b)
    {
        if (a == b) return null;
        int ra = NumRank(a), rb = NumRank(b);
        if (ra < 0 || rb < 0) return null;
        return ra >= rb ? a : b;
    }

    void ConvTo(Type t)
    {
        if (t == Bcl("System.Double")) _il.Emit(OpCodes.Conv_R8);
        else if (t == Bcl("System.Single")) _il.Emit(OpCodes.Conv_R4);
        else if (t == Bcl("System.Int64")) _il.Emit(OpCodes.Conv_I8);
        else if (t == Bcl("System.UInt64")) _il.Emit(OpCodes.Conv_U8);
        else if (t == Bcl("System.Int32")) _il.Emit(OpCodes.Conv_I4);
        else if (t == Bcl("System.UInt32")) _il.Emit(OpCodes.Conv_U4);
    }

    Type EmitBin(JsonElement e)
    {
        var op = e.GetProperty("op").GetString();
        var lt = EmitExpr(e.GetProperty("lhs"));
        var rt = EmitExpr(e.GetProperty("rhs"));
        // Mixed numeric operands (e.g. `Double / Int`, `Int + Long`) -> coerce both to the wider type. Shifts keep
        // their int shift-amount operand, so they're excluded.
        if (op != "<<" && op != ">>" && op != ">>>")
        {
            var common = NumericCommon(lt, rt);
            if (common != null)
            {
                if (rt != common) ConvTo(common);                       // coerce r (top of stack)
                if (lt != common)                                       // coerce l (below r): stash r, conv l, restore
                {
                    var tmp = _il.DeclareLocal(common);
                    _il.Emit(OpCodes.Stloc, tmp); ConvTo(common); _il.Emit(OpCodes.Ldloc, tmp);
                }
                lt = common;
            }
        }
        // Unsigned operands (Kotlin UInt/ULong -> .NET uint/ulong) need the UNSIGNED CIL ops for division and
        // remainder (a direct `bin` on the raw unsigned operand). Reads the CIR operand type only -- no Kotlin
        // knowledge. Without this, `a / b` on UInt >= 2^31 is silently wrong (signed Div on the bit pattern).
        // NOTE: ordered compares are NOT here -- Kotlin lowers `a > b` on UInt to `a.compareTo(b) > 0`, where
        // compareTo does the UNSIGNED compare and the outer `> 0` is a plain signed int compare. (`byte`/`ushort`
        // arithmetic promotes to UInt, so only uint/ulong reach a direct unsigned div here.)
        bool isUns = lt == Bcl("System.UInt32") || lt == Bcl("System.UInt64");
        // Float/double `<=`/`>=` need the UNORDERED-inverted compare (C#'s shape): `a <= b` == !(a > b treating
        // unordered as TRUE) -> `cgt.un; ldc.i4.0; ceq` (resp. `>=` -> `clt.un; ...`). The plain signed cgt/clt
        // inversion returns TRUE for a NaN operand (`NaN <= 1.0` was True) because cgt/clt yield 0 on unordered
        // and the inversion flips it. `<`/`>` stay ordered clt/cgt (0 on unordered = correct false), and integer
        // paths keep the signed opcodes (unsigned compares never reach a direct bin — see the note above).
        bool isFloat = lt == Bcl("System.Single") || lt == Bcl("System.Double");
        switch (op)
        {
            case "+": _il.Emit(OpCodes.Add); return lt;
            case "-": _il.Emit(OpCodes.Sub); return lt;
            case "*": _il.Emit(OpCodes.Mul); return lt;
            // Signed integer `/` and `%` by -1 overflow the raw CIL `div`/`rem` opcode at MinValue (the CLR throws
            // OverflowException on `MinValue / -1`), but Kotlin's integer division WRAPS: `MIN / -1 == MIN`, `x % -1 == 0`.
            // Guard the divisor==-1 case with identities that also cover MinValue: `x / -1 == -x` (CIL `neg` wraps
            // MinValue, no overflow) and `x % -1 == 0` for every x. Unsigned/float never overflow here — raw opcode.
            case "/":
                if (!isUns && (lt == Bcl("System.Int32") || lt == Bcl("System.Int64"))) { EmitDivRemGuarded(isRem: false, lt); return lt; }
                _il.Emit(isUns ? OpCodes.Div_Un : OpCodes.Div); return lt;
            case "%":
                if (!isUns && (lt == Bcl("System.Int32") || lt == Bcl("System.Int64"))) { EmitDivRemGuarded(isRem: true, lt); return lt; }
                _il.Emit(isUns ? OpCodes.Rem_Un : OpCodes.Rem); return lt;
            case "&": _il.Emit(OpCodes.And); return lt;
            case "|": _il.Emit(OpCodes.Or); return lt;
            case "^": _il.Emit(OpCodes.Xor); return lt;
            case "<<": _il.Emit(OpCodes.Shl); return lt;
            case ">>": _il.Emit(OpCodes.Shr); return lt;
            case ">>>": _il.Emit(OpCodes.Shr_Un); return lt;
            case "==": _il.Emit(OpCodes.Ceq); return Bcl("System.Boolean");
            case "!=": _il.Emit(OpCodes.Ceq); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return Bcl("System.Boolean");
            case "<": _il.Emit(OpCodes.Clt); return Bcl("System.Boolean");
            case ">": _il.Emit(OpCodes.Cgt); return Bcl("System.Boolean");
            case "<=": _il.Emit(isFloat ? OpCodes.Cgt_Un : OpCodes.Cgt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return Bcl("System.Boolean");
            case ">=": _il.Emit(isFloat ? OpCodes.Clt_Un : OpCodes.Clt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return Bcl("System.Boolean");
            default: throw new NotSupportedException("bin " + op);
        }
    }

    // Emit signed integer `/`/`%` with the divisor==-1 guard (stack on entry: [dividend, divisor]; leaves [result]).
    // Kotlin's integer division wraps at MinValue; the raw CIL `div`/`rem` throws OverflowException on `MinValue / -1`.
    // Since `x / -1 == -x` (CIL `neg` wraps MinValue) and `x % -1 == 0` for all x, we branch on divisor==-1 and use the
    // wrapping identity, dodging the overflow entirely without a MinValue comparison. `t` is int or long.
    void EmitDivRemGuarded(bool isRem, Type t)
    {
        var divisor = _il.DeclareLocal(t);
        _il.Emit(OpCodes.Stloc, divisor);       // stack: [dividend]
        var normal = _il.DefineLabel();
        var done = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, divisor);
        _il.Emit(OpCodes.Ldc_I4_M1);
        if (t == Bcl("System.Int64")) _il.Emit(OpCodes.Conv_I8);
        _il.Emit(OpCodes.Bne_Un, normal);       // divisor != -1 -> normal path (stack: [dividend])
        // divisor == -1: result is -dividend (div) or 0 (rem)
        if (isRem) { _il.Emit(OpCodes.Pop); _il.Emit(OpCodes.Ldc_I4_0); if (t == Bcl("System.Int64")) _il.Emit(OpCodes.Conv_I8); }
        else _il.Emit(OpCodes.Neg);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(normal);                  // stack: [dividend]
        _il.Emit(OpCodes.Ldloc, divisor);
        _il.Emit(isRem ? OpCodes.Rem : OpCodes.Div);
        _il.MarkLabel(done);
    }

    Type EmitUn(JsonElement e)
    {
        var op = e.GetProperty("op").GetString();
        var t = EmitExpr(e.GetProperty("e"));
        switch (op)
        {
            case "-": _il.Emit(OpCodes.Neg); return t;
            case "+": return t;
            case "~": _il.Emit(OpCodes.Not); return t;
            case "!": _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return Bcl("System.Boolean");
            default: throw new NotSupportedException("un " + op);
        }
    }

    // A CIR `conv` instruction -> the matching CIL conv opcode; returns the target CLR type. ilemit only selects the
    // opcode for the requested target width — WHERE a Kotlin numeric conversion becomes a `conv` node is bir2cir's call.
    Type EmitConv(JsonElement e)
    {
        EmitExpr(e.GetProperty("e"));
        var to = PrimShorthandName(SlotName(e.GetProperty("to")));
        switch (to)
        {
            case "int" or "kotlin.Int": _il.Emit(OpCodes.Conv_I4); return Bcl("System.Int32");
            case "long" or "kotlin.Long": _il.Emit(OpCodes.Conv_I8); return Bcl("System.Int64");
            case "double" or "kotlin.Double": _il.Emit(OpCodes.Conv_R8); return Bcl("System.Double");
            case "float" or "kotlin.Float": _il.Emit(OpCodes.Conv_R4); return Bcl("System.Single");
            case "short" or "kotlin.Short": _il.Emit(OpCodes.Conv_I2); return Bcl("System.Int16");
            case "sbyte" or "kotlin.Byte": _il.Emit(OpCodes.Conv_I1); return Bcl("System.SByte");
            case "char" or "kotlin.Char": _il.Emit(OpCodes.Conv_U2); return Bcl("System.Char");
            // Unsigned targets (#71): zero-extending/truncating conversions. `byte` = Kotlin UByte (System.Byte),
            // `ushort` = UShort, `uint` = UInt, `ulong` = ULong — the shorthand/alias split is PrimShorthandName's.
            // Needed by the #93 narrow-widening conv (UByte/UShort arith -> UInt) and any `.toUByte()/.toUInt()`.
            case "byte" or "kotlin.UByte": _il.Emit(OpCodes.Conv_U1); return Bcl("System.Byte");
            case "ushort" or "kotlin.UShort": _il.Emit(OpCodes.Conv_U2); return Bcl("System.UInt16");
            case "uint" or "kotlin.UInt": _il.Emit(OpCodes.Conv_U4); return Bcl("System.UInt32");
            case "ulong" or "kotlin.ULong": _il.Emit(OpCodes.Conv_U8); return Bcl("System.UInt64");
            default: throw new NotSupportedException("conv " + to);
        }
    }

    // Array literal (`intArrayOf(...)` / `arrayOf(...)`) -> newarr + per-element stelem.
    Type EmitNewArray(JsonElement e)
    {
        var elem = MapType(e.GetProperty("elem"));
        var elems = e.GetProperty("elems").EnumerateArray().ToList();
        _il.Emit(OpCodes.Ldc_I4, elems.Count);
        _il.Emit(OpCodes.Newarr, elem);
        for (int i = 0; i < elems.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            EmitArrayElemCoerced(elems[i], elem);
            EmitStelem(elem);
        }
        return elem.MakeArrayType();
    }

    // Coerce an array-element value to the array's element type before `stelem` (C2: `Array<Int?>` = `Nullable<int>[]`).
    // `arrayOf(1, null, 3)` / `arr[i] = 5` push a BARE `int` (or a null literal) into a `Nullable<int>` slot — without
    // the `T -> Nullable<T>` wrap (or `default(Nullable<T>)` for null) `stelem Nullable<int>` stores raw int bits as a
    // Nullable struct -> memory corruption / SIGSEGV. ONLY a genuine `Nullable<>` element takes the wrap path (the
    // EmitNullableCoerced `T -> Nullable<T>` / null-default); every other element keeps the pre-existing box-only
    // behavior (a value into a reference element `Array<Any?>` / `object[]`), so a `gp:T`-element array
    // (AbstractCollection.toArray's `newarr !T`) is UNTOUCHED — routing it through the broad EmitNullableCoerced would
    // spuriously unbox.any an object element into the `gp:T` slot (regressed the collection/map stdlib emit).
    void EmitArrayElemCoerced(JsonElement value, Type elem)
    {
        if (elem.IsGenericType && elem.GetGenericTypeDefinition() == Bcl("System.Nullable`1")) { EmitNullableCoerced(value, elem); return; }
        var et = EmitExpr(value);
        if (et != null && NeedsBoxToRef(et) && !IsValueType(elem) && !elem.IsGenericParameter) _il.Emit(OpCodes.Box, et);
    }

    Type EmitConcat(JsonElement e)
    {
        var parts = e.GetProperty("parts").EnumerateArray().ToList();
        _il.Emit(OpCodes.Ldc_I4, parts.Count);
        _il.Emit(OpCodes.Newarr, Bcl("System.Object"));
        for (int i = 0; i < parts.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var t = EmitExpr(parts[i]);
            if (NeedsBoxToRef(t)) _il.Emit(OpCodes.Box, t);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("String.ConcatArray"));
        return Bcl("System.String");
    }

    // Emit an expression, coercing a bare `T` (or a null literal) to `Nullable<T>` when `want` is a Nullable<T>.
    // Shared by EmitArg and EmitCond so value-type `T?` flows correctly through args and if/when branches.
    Type EmitNullableCoerced(JsonElement node, Type want)
    {
        bool wantNullable = want != null && want.IsGenericType && want.GetGenericTypeDefinition() == Bcl("System.Nullable`1");
        if (wantNullable && node.TryGetProperty("k", out var k) && k.GetString() is "const" or "clr.const"
            && node.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Null)
        {
            var loc = _il.DeclareLocal(want);
            _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, want); _il.Emit(OpCodes.Ldloc, loc);
            return want;
        }
        var got = EmitExpr(node);
        if (wantNullable && got != null && want.GetGenericArguments()[0] == got)
        {
            // want = Nullable<got>. When got is an EMITTED value type (a TypeBuilder), Reflection.Emit can't resolve
            // the ctor on the MakeGenericType — use TypeBuilder.GetConstructor(constructed, open Nullable<>'s ctor).
            // The declaration is fixed — `Nullable<T>..ctor(T)` — and the owner this coercion computed is what
            // it gets anchored onto. Same shape as every other open declaration named once and anchored per use.
            var ctor = AnchorConstructor(want, WellKnown<ConstructorInfo>("NullableT.ctor"));
            EmitConstructor(_il, OpCodes.Newobj, ctor);
            return want;
        }
        // A value-type / generic-param branch flowing into an `object` want (an erased generic `T?` return whose
        // branch type-tag was retyped to object by bir2cir's NullableGenericErasure) must box; a `null` branch
        // already left a real null ref (EmitExpr(null-const) is a reference), so it is unaffected.
        if (want == Bcl("System.Object") && got != null && NeedsBoxToRef(got)) { _il.Emit(OpCodes.Box, got); return want; }
        return got;
    }

    // A cond/when BRANCH coerced to the result type `want`: EmitNullableCoerced (T -> Nullable<T> / null-default /
    // box-to-object) PLUS the REVERSE (C2) — a REFERENCE branch (`object`, the erased nullable-generic map read
    // `clrMapGet<K,V>:object`) flowing into a VALUE-type / generic-param `want`. `Map.getOrElse`/`getOrPut` return a
    // `cond` typed `gp:V` whose `else` branch is the object-typed `value`/`__subj` local; without the universal
    // `unbox.any <want>` the reference sits where a value/`!!V` is expected -> a value reinterpreted from a reference
    // -> garbage. Scoped to cond branches (not the shared EmitNullableCoerced) so ordinary object->gp stores are untouched.
    Type EmitBranchCoerced(JsonElement node, Type want)
    {
        var got = EmitNullableCoerced(node, want);
        // A VOID-producing branch (a statement-like arm: `x.close()`/`println(...)`/a Unit `const void`, or a
        // value-producing `try` whose arms are void) under a VALUE-producing conditional — a `kotlin.Unit`-typed
        // `when` in expression position, whose OTHER arms push one value (e.g. a `valueBlock` yielding a Unit local).
        // This arm pushes NOTHING, so the branches merge at the cond-end with inconsistent stack depth (ilverify
        // PathStackDepth / StackUnderflow; InvalidProgramException at JIT). Push a default of `want` so every path
        // leaves exactly one value. Pure stack-depth reconciliation — no Kotlin semantics (Unit resolves to a plain
        // reference type here, and sibling Unit arms already push an uninitialized-local reference).
        if (got == Bcl("System.Void") && want != null && want != Bcl("System.Void"))
        {
            if (IsValueType(want) || want.IsGenericParameter)
            { var d = _il.DeclareLocal(want); _il.Emit(OpCodes.Ldloca, d); _il.Emit(OpCodes.Initobj, want); _il.Emit(OpCodes.Ldloc, d); }
            else _il.Emit(OpCodes.Ldnull);
            return want;
        }
        if (want != null && got != null && !IsValueType(got) && !got.IsGenericParameter && got != want
            && (IsValueType(want) || want.IsGenericParameter)) { _il.Emit(OpCodes.Unbox_Any, want); return want; }
        return got;
    }

    Type EmitCond(JsonElement e)
    {
        // A value-type-nullable if/when (`Int?`) tags its result type so each branch's `T`/`null` coerces to Nullable<T>.
        Type want = null;
        if (e.TryGetProperty("type", out var tt)) { try { want = ClrRef(tt); } catch { } }
        var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
        EmitExpr(e.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, elseL);
        var t = EmitBranchCoerced(e.GetProperty("then"), want); _il.Emit(OpCodes.Br, end);
        _il.MarkLabel(elseL); EmitBranchCoerced(e.GetProperty("else"), want); _il.MarkLabel(end);
        return want ?? t;
    }

    // Kotlin structural `==`: `if (a == null) b == null else a.Equals((object)b)`.
    // Value types are boxed first — boxing a Nullable<T> with HasValue=false yields a real null ref,
    // so the same null-safe shape works for `Int?` as for reference types.
    Type EmitObjMethod(JsonElement e)
    {
        // Kotlin Any-method on a builtin receiver -> System.Object virtual (box value types first).
        var rt = EmitExpr(e.GetProperty("recv"));
        if (NeedsBoxToRef(rt)) _il.Emit(OpCodes.Box, rt);
        switch (e.GetProperty("method").GetString())
        {
            case "GetHashCode": EmitMethod(_il, OpCodes.Callvirt, WellKnown<MethodInfo>("Object.GetHashCode")); return Bcl("System.Int32");
            case "ToString": EmitMethod(_il, OpCodes.Callvirt, WellKnown<MethodInfo>("Object.ToString")); return Bcl("System.String");
            case "Equals":
                var at = EmitExpr(e.GetProperty("arg"));
                if (NeedsBoxToRef(at)) _il.Emit(OpCodes.Box, at);
                EmitMethod(_il, OpCodes.Callvirt, WellKnown<MethodInfo>("Object.Equals"));
                return Bcl("System.Boolean");
        }
        return Bcl("System.Object");
    }

    Type EmitObjEq(JsonElement e)
    {
        var nonNull = _il.DefineLabel();
        var done = _il.DefineLabel();
        var lt = EmitExpr(e.GetProperty("lhs"));
        if (NeedsBoxToRef(lt)) _il.Emit(OpCodes.Box, lt);
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Brtrue, nonNull);
        _il.Emit(OpCodes.Pop);                                   // a is null -> result = (b == null)
        var rt1 = EmitExpr(e.GetProperty("rhs"));
        if (NeedsBoxToRef(rt1)) _il.Emit(OpCodes.Box, rt1);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Ceq);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(nonNull);                                  // a non-null -> a.Equals((object)b)
        var rt2 = EmitExpr(e.GetProperty("rhs"));
        if (NeedsBoxToRef(rt2)) _il.Emit(OpCodes.Box, rt2);
        EmitMethod(_il, OpCodes.Callvirt, WellKnown<MethodInfo>("Object.Equals"));
        _il.MarkLabel(done);
        return Bcl("System.Boolean");
    }

}
