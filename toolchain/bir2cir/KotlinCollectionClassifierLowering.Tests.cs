using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static KotlinRuntimeTypeWitness;

static partial class KotlinCollectionClassifierLowering
{
    internal static void SelfTest()
    {
        var alias = new TypeNode.Fqn("VariantAlias", new TypeNode[] { new TypeNode.Star() });
        var any = new TypeNode.Fqn("kotlin.Any");
        Check(OpaqueTestType(alias) == any && OpaqueTestType(new TypeNode.Nullable(alias)) == new TypeNode.Nullable(any)
            && OpaqueTestType(new TypeNode.Oblivious(alias)) == new TypeNode.Oblivious(any), "opaque targets must retain source nullability");
        var refs = ReferenceMetadataIndex.CollectionClassifierTestIndex();
        var variable = new TypeNode.Tv("method", 0);
        var ordinary = new BirCast { Type = variable, ReifiedTypeOperand = false, E = new BirLocal { Name = "value" } };
        var array = new BirIsInstance { Type = new TypeNode.Array(variable), E = new BirLocal { Name = "array" } };
        var plain = new BirCast { Type = any, ReifiedTypeOperand = true, E = new BirLocal { Name = "plain" } };
        var listType = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.collections.MutableList", new TypeNode[] { any }));
        var cast = new BirCast { Type = listType, Sty = listType, ReifiedTypeOperand = false,
            E = new BirCallStatic { Method = "castEffect" } };
        var test = new BirIsInstance { Type = listType, E = new BirCallStatic { Method = "testEffect" } };
        var safe = new BirInstanceOrNull { Type = variable, E = new BirCallStatic { Method = "safeEffect" },
            ReifiedTypeOperand = true, KotlinTypeWitness = KotlinTypeWitness.ConstantValue(8) };
        var body = new IBirStatement[] {
            new BirExpressionStatement { Expr = ordinary }, new BirExpressionStatement { Expr = array },
            new BirExpressionStatement { Expr = plain }, new BirExpressionStatement { Expr = cast },
            new BirExpressionStatement { Expr = test }, new BirExpressionStatement { Expr = safe },
        };
        var method = new BirFunctionDeclaration { Name = "probe", TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Body = body };
        var root = new BirFileDeclaration { Fields = new[] { new BirFieldDeclaration { Name = "dotkt$collectionClassifier$value$0" } }, Methods = new[] { method } };
        Apply(root, refs);
        Check(ReferenceEquals(method.Body.Value, body) && ordinary.ReifiedTypeOperand.Value == false && !plain.ReifiedTypeOperand.IsPresent
            && ReferenceEquals(((BirExpressionStatement)body[0]).Expr.Value, ordinary) && ReferenceEquals(((BirExpressionStatement)body[1]).Expr.Value, array),
            "ordinary generic and array operations must retain their later ownership while unrelated markers are consumed");
        var candidate = (BirCallStatic)cast.E.Value;
        var lastStorage = (BirCallStatic)candidate.Args.Value[0];
        var firstStorage = (BirCallStatic)lastStorage.Args.Value[0];
        Check(candidate.Method.Value == CastCandidate && candidate.Args.Value.Length == 11 && candidate.Sig.Value.Length == 11
            && ((TypeNode.Fqn)((BirClassReference)lastStorage.Args.Value[2]).Type.Value).Name == "Storage.Second"
            && ((TypeNode.Fqn)((BirClassReference)firstStorage.Args.Value[2]).Type.Value).Name == "Storage.First"
            && firstStorage.Args.Value[0] is BirCallStatic { Method.Value: "castEffect" }
            && cast.Type.Value == new TypeNode.Nullable(any) && cast.Sty.Value == cast.Type.Value,
            "cast candidate nesting must preserve storage order, original effect and opaque physical nullability");
        var testBlock = (BirValueBlock)((BirExpressionStatement)body[4]).Expr.Value;
        var safeBlock = (BirValueBlock)((BirExpressionStatement)body[5]).Expr.Value;
        Check(((BirVariable)testBlock.Stmts.Value[0]).Name.Value == "dotkt$collectionClassifier$value$1"
            && ((BirVariable)safeBlock.Stmts.Value[0]).Name.Value == "dotkt$collectionClassifier$value$2"
            && ((BirConditional)safeBlock.Result.Value).Type.Value == new TypeNode.Fqn("object"),
            "test allocators must avoid names and nullable generic safe casts must use boxed result storage");
        var nodes = new List<BirGraphValue>(); root.Visit(nodes.Add);
        Check(nodes.OfType<BirCallStatic>().Count(call => call.Method.OrDefault() == "testEffect") == 1
            && nodes.OfType<BirCallStatic>().Count(call => call.Method.OrDefault() == "safeEffect") == 1
            && nodes.OfType<BirCallStatic>().Where(call => call.Method.OrDefault() == "kotlinCollectionStorageMatches")
                .All(call => call.Args.Value.Length == 8 && call.Sig.Value.Length == 8)
            && nodes.Where(ClassifierOperation.Admits).All(node => !new ClassifierOperation(node).Flags.IsPresent),
            "collection guards must evaluate effects once and consume flags while carrying complete helper descriptors");
        var originalCandidate = cast.E.Value;
        Apply(root, refs);
        Check(ReferenceEquals(cast.E.Value, originalCandidate), "an already materialized cast candidate must not be wrapped again");
        Console.WriteLine("[collection classifiers] self-test OK (typed membership, boxed safe casts, storage order, opaque targets, once evaluation)");
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Typed collection classifier: " + message); }
}

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex CollectionClassifierTestIndex()
    {
        var index = Build(new[] { typeof(object).Assembly.Location });
        index._ownerAlias["kotlin.collections.MutableList"] = "System.Collections.Generic.IList";
        index._ownerTypeParamDeclarations["kotlin.collections.MutableList"] = "[{\"name\":\"T\"}]";
        index._kotlinMapStorageDefinitions = new[] { "Storage.First", "Storage.Second" };
        return index;
    }
}
