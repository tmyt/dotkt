using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Fold names while their Kotlin identities are still available; never drop evaluation of a receiver.
// Dynamic and reified literals instead resolve against the runtime declaration's classifier metadata.
// CLR storage tokens (including the element type used for array allocation) are not Kotlin literals.
static class KClassMemberBinding
{
    // Kotlin builtin types that are FINAL (no subtypes), so a bound `value::class` on one is statically resolvable
    // (runtime class == static type) and const-folding to the Kotlin name is sound. Most are also CLR-RENAMED (the
    // primitive tower -> System.Int32/…, String -> System.String, the specialized arrays -> int[]/…), so the fold is
    // what restores the Kotlin name; Unit/Nothing keep their Kotlin name but are included so a `Unit` local's `::class`
    // folds uniformly. (`kotlin.Array<T>` is a distinct `{t:array}` token, not an Fqn — its bound case is a follow-up.)
    static readonly HashSet<string> KnownFinal = new()
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
        "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
        "kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char",
        "kotlin.String", "kotlin.Unit", "kotlin.Nothing",
        "kotlin.IntArray", "kotlin.LongArray", "kotlin.ShortArray", "kotlin.ByteArray",
        "kotlin.DoubleArray", "kotlin.FloatArray", "kotlin.BooleanArray", "kotlin.CharArray",
        "kotlin.UIntArray", "kotlin.ULongArray", "kotlin.UShortArray", "kotlin.UByteArray",
    };

    internal static BirAttribute Identity(string token, string declaration, string name, bool local) => new() {
        Attr = new TypeNode.Fqn(RoundtripMetadata.RuntimeClassifierAttribute),
        ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.Type"), new TypeNode.Fqn("System.Type"),
            new TypeNode.Fqn("System.String"), new TypeNode.Fqn("System.Boolean") },
        Args = new IBirAttributeValue[] {
            new BirClassReference { Type = new TypeNode.Fqn(token), DefinitionToken = true },
            new BirClassReference { Type = new TypeNode.Fqn(declaration), DefinitionToken = true },
            new BirConstant { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(name) },
            new BirConstant { Type = new TypeNode.Fqn("System.Boolean"), Value = new BirLiteral.BooleanLiteral(local) },
        },
    };

    internal static void BindLocalDeclarations(BirFileDeclaration root, IReadOnlyDictionary<string, string> carriers)
    {
        foreach (var type in root.Types.OrEmpty().OfType<BirClassDefinition>()) {
            if (type.LocalClassifierName.OrDefault() is not string sourceName) continue;
            var owner = type.Name.Value;
            type.Attrs = type.Attrs.OrEmpty().Append(Identity(carriers.GetValueOrDefault(owner, owner), owner, sourceName, true)).ToArray();
            type.LocalClassifierName = default;
        }
    }

    public static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs = null)
    {
        var locals = root.Types.OrEmpty().OfType<BirClassDefinition>().Where(type => type.LocalClassifierName.HasValue)
            .Select(type => type.Name.Value).ToHashSet(StringComparer.Ordinal);
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload or BirAttribute) return node;
            if (node is BirCallInstance call && call.OwnerType.OrDefault() is TypeNode.Fqn { Name: "kotlin.reflect.KClass" }
                && call.Prop.OrDefault() == "get" && call.Method.OrDefault() is "simpleName" or "qualifiedName"
                && call.Recv.OrDefault() is IBirExpression receiver && StaticKotlinFqn(receiver, refs) is string name
                && !locals.Contains(name))
                return new BirConstant { Type = new TypeNode.Fqn("kotlin.String"),
                    Value = new BirLiteral.TextLiteral(call.Method.Value == "simpleName" ? SimpleOf(name) : name) };
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            if (node is BirClassReference source) {
                ReflectedKotlinType.BindKnownLiteral(source);
                if (source.ReifiedLiteral.OrDefault() && KotlinTypeWitness.Variable(source.Type.Value) != null)
                    return source; // The witness pass owns a still-open reified argument.
            }
            IBirExpression operand;
            if (node is BirRuntimeClass runtime) operand = runtime;
            else if (node is BirClassReference literal && (literal.ReifiedLiteral.OrDefault()
                || literal.Type.OrDefault() is TypeNode.Fqn f && locals.Contains(f.Name))) {
                literal.ReifiedLiteral = default;
                operand = literal;
            } else return node;
            var type = new TypeNode.Fqn("System.Type");
            return new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.ReflectionRuntimeKt"),
                Method = "runtimeKClass", Sig = new TypeNode[] { type }, Ret = type, Args = new[] { operand } };
        }
        Walk(root);
    }

    // The statically-known Kotlin FQN a `::class` receiver names, or null when the class is only known at run time.
    static string StaticKotlinFqn(IBirExpression recv, ReferenceMetadataIndex refs) => recv switch
    {
        // Unbound class literal — the token IS the literal type; always resolvable (finality is irrelevant, it is a type,
        // not a reflected instance).
        BirClassReference value => FqnOf(value.Type.OrDefault(), refs),
        // Bound `value::class` — resolvable iff the value's static type is a known-final builtin (runtime class == static
        // type) AND folding the receiver away drops no evaluation. That last test is Q2 of the value questions
        // (bir-common/ValueStability.cs), asked here for the same reason CallEvalLowering asks it of a binding nothing
        // reads: this fold makes the receiver unread, so it may only be removed where evaluating it is unobservable.
        // A classRef names its referent, not its System.Type value: folding
        // (Int::class)::class as though that token were an Int would be wrong.
        BirRuntimeClass value when value.E.OrDefault() is BirGraphNode e && ValueStability.IsDroppable(e.Tag)
            && e is not BirClassReference
            && FqnOf(e.ResultTypeStamp ?? e.ExplicitSourceType, refs) is string st && KnownFinal.Contains(st) => st,
        _ => null,
    };

    // The Kotlin FQN a type slot names — the bare Fqn, or a nullable-of-Fqn unwrapped to its core (an inline-spliced
    // `T::class` with a nullable T arg, or a `String?` receiver; the Kotlin answer is the non-null identity's name).
    // Null for any other token shape (tv/array/fn), which the caller routes to the run-time read instead of folding.
    static string FqnOf(TypeNode typeNode, ReferenceMetadataIndex refs) => typeNode switch
    {
        TypeNode.Fqn f => ReflectedKotlinType.CollectionLiteralName(f.Name)
            ?? (refs?.TryExistentialPhysicalOwner(f.Name, out _) == true ? ReflectedKotlinType.SourceName(f) : f.Name),
        TypeNode.Nullable nullable => FqnOf(nullable.Of, refs),
        _ => null,
    };

    // simpleName = the segment after the last `.` (a nested class's `Outer.Inner` -> `Inner`); the whole name otherwise.
    static string SimpleOf(string fqn)
    {
        int dot = fqn.LastIndexOf('.');
        return dot >= 0 ? fqn.Substring(dot + 1) : fqn;
    }

    internal static void SelfTest()
    {
        BirCallInstance Name(IBirExpression receiver) => new() { OwnerType = new TypeNode.Fqn("kotlin.reflect.KClass"),
            Method = "simpleName", Prop = "get", Recv = OptionalField<IBirExpression>.Of(receiver) };
        var effect = Name(new BirRuntimeClass { E = new BirCallStatic { Method = "effect", Sty = new TypeNode.Fqn("kotlin.String") } });
        var token = Name(new BirRuntimeClass { E = new BirClassReference { Type = new TypeNode.Fqn("kotlin.Int") } });
        var statements = new[] {
            new BirExpressionStatement { Expr = Name(new BirClassReference { Type = new TypeNode.Nullable(new TypeNode.Fqn("Probe.Outer.Inner")) }) },
            new BirExpressionStatement { Expr = Name(new BirRuntimeClass { E = new BirLocal { Name = "n", Sty = new TypeNode.Fqn("kotlin.Int") } }) },
            new BirExpressionStatement { Expr = effect }, new BirExpressionStatement { Expr = token },
        };
        var method = new BirFunctionDeclaration { Name = "use", Body = statements.Cast<IBirStatement>().ToArray() };
        var root = new BirFileDeclaration { Methods = new[] { method } };
        Apply(root);
        if (!ReferenceEquals(root.Methods.Value[0], method)
            || statements[0].Expr.Value is not BirConstant { Value.Value: BirLiteral.TextLiteral { Value: "Inner" } }
            || statements[1].Expr.Value is not BirConstant { Value.Value: BirLiteral.TextLiteral { Value: "Int" } }
            || !ReferenceEquals(statements[2].Expr.Value, effect) || !ReferenceEquals(statements[3].Expr.Value, token))
            throw new InvalidOperationException("Typed KClass fold lost identity, Kotlin names, receiver evaluation or token type semantics");
    }
}
