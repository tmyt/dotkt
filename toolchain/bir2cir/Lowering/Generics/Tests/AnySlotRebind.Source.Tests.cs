using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class AnySlotRebind
{
    internal static void SelfTestSource()
    {
        var refs = ReferenceMetadataIndex.ObjectSlotUseTestIndex();
        var source = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Unresolved", Kind = "class" },
            new BirClassDefinition { Name = "Declared", Kind = "class", Methods = new[] {
                new BirFunctionDeclaration { Name = "toString", ObjectOverride = true },
            } },
            new BirClassDefinition { Name = "Inherited", Kind = "class", Base = new TypeNode.Fqn("Declared") },
            new BirClassDefinition { Name = "NativeBase", Kind = "class", Base = new TypeNode.Fqn("ReferencedBase") },
        } };
        var types = CollectLocalTypes(new[] { source });
        BirCallInstance Call(string owner, string method = "ToString") => new() {
            OwnerType = new TypeNode.Fqn(owner), Method = method,
            Recv = new BirCallStatic { Method = "receiverEffect", Args = Array.Empty<IBirExpression>() }, AnySlot = true,
            Args = method == "Equals" ? new IBirExpression[] {
                new BirCallStatic { Method = "argumentEffect", Args = Array.Empty<IBirExpression>() },
            } : Array.Empty<IBirExpression>(),
            DeclarationId = "selected-source-slot", Sty = new TypeNode.Fqn(method == "ToString" ? "kotlin.String" : "kotlin.Int"),
        };
        var toString = Call("Unresolved");
        var equals = Call("Unresolved", "Equals");
        var hash = Call("Unresolved", "GetHashCode");
        var declared = Call("Declared");
        var inherited = Call("Inherited");
        var nativeBase = Call("NativeBase");
        var external = Call("UnrelatedExternal");
        var imported = Call("Imported.Semantic");
        var ordinaryArray = Call("kotlin.Array");
        var primitiveArray = Call("kotlin.IntArray");
        var super = Call("Unresolved", "Equals"); super.Super = true;
        var ordinarySuper = Call("Unresolved", "Equals"); ordinarySuper.Super = true; ordinarySuper.AnySlot = default;
        var calls = new[] { toString, equals, hash, declared, inherited, nativeBase, external, imported,
            ordinaryArray, primitiveArray, super, ordinarySuper };
        var statements = calls.Select(call => new BirExpressionStatement { Expr = call }).ToArray();
        var body = statements.Cast<IBirStatement>().ToArray();
        var method = new BirFunctionDeclaration { Name = "use", Body = body };
        var file = new BirFileDeclaration { FileClass = "Use", Methods = new[] { method } };
        Require(ReferenceEquals(Apply(file, types, refs), file) && ReferenceEquals(method.Body.Value, body),
            "root and original declaration vectors stay owned");
        for (var i = 0; i < 3; i++)
            Require(statements[i].Expr.Value is BirObjectMethod result && !result.AnySlot.IsPresent
                && result.Sty.Equals(calls[i].Sty) && ReferenceEquals(result.Recv.OrDefault(), calls[i].Recv.OrDefault())
                && (i == 1 ? ReferenceEquals(result.Arg.OrDefault(), calls[i].Args.Value[0]) : !result.Arg.IsPresent),
                "unresolved local slots transfer actual operands without duplicating evaluation");
        Require(ReferenceEquals(statements[3].Expr.Value, declared) && !declared.AnySlot.IsPresent
            && ReferenceEquals(statements[4].Expr.Value, inherited) && !inherited.AnySlot.IsPresent
            && ReferenceEquals(statements[5].Expr.Value, nativeBase) && !nativeBase.AnySlot.IsPresent
            && ReferenceEquals(statements[6].Expr.Value, external) && !external.AnySlot.IsPresent,
            "declared, inherited and foreign owners retain their call route and consume only ToString intent");
        Require(statements[7].Expr.Value is BirObjectMethod importedResult && !importedResult.AnySlot.IsPresent
            && ReferenceEquals(importedResult.Recv.OrDefault(), imported.Recv.OrDefault()), "selected reference metadata owns existential Object dispatch");
        Require(ordinaryArray.AnySlot.Value && primitiveArray.AnySlot.Value, "native array Object slots retain helper exclusion intent");
        Require(ReferenceEquals(statements[10].Expr.Value, super) && super.Super.Value && super.AnySlot.Value
            && super.OwnerType.OrDefault() == new TypeNode.Fqn("kotlin.Any")
            && ordinarySuper.OwnerType.OrDefault() == new TypeNode.Fqn("Unresolved"), "semantic super retarget stays nonvirtual and does not capture an ordinary super call");

        // The existing equality owner is the next consumer, not a second slot
        // policy in these passes. Super dispatch must stay nonvirtual while the
        // consumed ordinary equality/hash slots use the established helpers.
        KotlinEqualityLowering.Apply(file);
        Require(statements[1].Expr.Value is BirCallStatic equality && equality.Method.OrDefault() == "kotlinInstanceEquals"
            && ReferenceEquals(equality.Args.Value[0], equals.Recv.OrDefault()) && ReferenceEquals(equality.Args.Value[1], equals.Args.Value[0])
            && statements[2].Expr.Value is BirCallStatic hashing && hashing.Method.OrDefault() == "kotlinInstanceHashCode"
            && super.Super.Value && !super.AnySlot.IsPresent && super.OwnerType.OrDefault() == new TypeNode.Fqn("kotlin.Any"),
            "the following equality consumer retains ordered values and nonvirtual super target");
        Console.WriteLine("[typed reference Object slots] self-test OK (exact module hierarchy, selected metadata, native arrays, super and evaluation order)");

        static void Require(bool condition, string site)
        { if (!condition) throw new InvalidOperationException("Reference Object slot source self-test failed: " + site); }
    }
}

// The same metadata/catalog construction as the reference-reader self-tests;
// no test mode or production binding fallback is involved.
sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex ObjectSlotUseTestIndex()
    {
        var metadata = new ReferenceDotKtMetadata();
        metadata.ExistentialPhysicalBySemanticOwner["Imported.Semantic"] = "Imported.Physical";
        return new ReferenceMetadataIndex(new List<ReferenceAssembly> { new("slots.dll", "slots", "1", metadata) },
            Build(Array.Empty<string>())._compileRefs);
    }
}
