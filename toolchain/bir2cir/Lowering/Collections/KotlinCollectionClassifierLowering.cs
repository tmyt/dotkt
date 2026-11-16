using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static KotlinRuntimeTypeWitness;

// A compiler-provided CLR storage face is not evidence of Kotlin MutableCollection membership.
// Apply the declaration-owned nominal guard while the tested Kotlin classifier is still explicit.
static partial class KotlinCollectionClassifierLowering
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    const string Matches = "kotlinCollectionMatches";
    const string CastCandidate = "kotlinCollectionCastCandidate";

    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        var names = ClassifierNames(root);
        var next = 0;
        var mapStorage = refs.KotlinMapStorageDefinitions();
        RewriteClassifierOperations(root, operation => Rewrite(operation, names, mapStorage, refs, ref next));
    }

    static BirGraphValue Rewrite(ClassifierOperation operation, HashSet<string> names, IReadOnlyList<string> mapStorage,
        ReferenceMetadataIndex refs, ref int next)
    {
        if (!operation.Operand.HasValue) return operation.Node;
        var operand = operation.Operand.Value;
        var operandType = operation.Type.OrDefault();
        while (operandType is TypeNode.Nullable or TypeNode.Oblivious)
            operandType = operandType is TypeNode.Nullable n ? n.Of : ((TypeNode.Oblivious)operandType).Of;
        // Reified array components and ordinary generic erasure belong to later
        // owners. Consume only the source marker this classifier stage owns.
        var ordinaryVariable = operandType is TypeNode.Tv && operation.Reified.HasValue && !operation.Reified.Value;
        if (operandType is not TypeNode.Array && !ordinaryVariable) operation.Reified = default;
        var witness = operation.Flags.HasValue ? CopyExpression(operation.Flags.Value) : null;
        operation.Flags = default;
        if (witness == null)
        {
            var flags = KotlinTypeWitness.Flags(operation.Type.OrDefault());
            if ((flags & ~1) == 0) return operation.Node;
            witness = KotlinTypeWitness.ConstantValue(flags);
        }
        var nullableObject = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var opaqueAlias = operandType is TypeNode.Fqn alias && AliasVarianceRepresentation.RequiresErasure(alias, refs);
        // A nominal/storage guard owns Kotlin membership. Its physical test
        // must use that same opaque alias ABI, not demand a new closed native face.
        void Project(ClassifierOperation test)
        {
            if (opaqueAlias) test.ProjectPhysicalType(OpaqueTestType(test.Type.OrDefault()));
        }
        var runtimeType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var boolean = new TypeNode.Fqn("kotlin.Boolean");
        BirClassReference ClassRef(string name) => new() { Type = new TypeNode.Fqn(name) };
        BirCallStatic Call(string helper, TypeNode result, IBirExpression value) => new() {
            Owner = new TypeNode.Fqn(RuntimeOwner), Method = helper, Ret = result,
            Sig = new TypeNode[] { nullableObject, integer }.Concat(Enumerable.Repeat<TypeNode>(runtimeType, 9)).ToArray(),
            Args = new IBirExpression[] { CopyExpression(value), CopyExpression(witness),
                ClassRef("System.Collections.Generic.IDictionary`2"), ClassRef("System.Collections.Generic.IReadOnlyDictionary`2"),
                ClassRef("System.Collections.Generic.ISet`1"), ClassRef("System.Collections.Generic.IReadOnlySet`1"),
                ClassRef("System.Collections.IList"), ClassRef("System.Collections.Generic.IList`1"),
                ClassRef("System.Collections.Generic.IReadOnlyList`1"), ClassRef("System.Collections.Generic.ICollection`1"),
                ClassRef("System.Collections.Generic.IReadOnlyCollection`1") },
        };
        BirCallStatic StorageCall(string helper, TypeNode result, IBirExpression value, string storage) => new() {
            Owner = new TypeNode.Fqn(RuntimeOwner), Method = helper, Ret = result,
            Sig = new TypeNode[] { nullableObject, integer }.Concat(Enumerable.Repeat<TypeNode>(runtimeType, 6)).ToArray(),
            Args = new IBirExpression[] { CopyExpression(value), CopyExpression(witness), ClassRef(storage),
                ClassRef("System.Collections.IList"), ClassRef("System.Collections.Generic.IList`1"),
                ClassRef("System.Collections.Generic.IReadOnlyList`1"), ClassRef("System.Collections.Generic.ISet`1"),
                ClassRef("System.Collections.Generic.IReadOnlySet`1") },
        };
        if (operation.IsCast)
        {
            if (operand is BirCallStatic { Method.Value: CastCandidate, Owner.HasValue: true } candidateCall
                && candidateCall.Owner.Value is TypeNode.Fqn { Name: RuntimeOwner }) return operation.Node;
            var candidate = operand;
            foreach (var storage in mapStorage)
                candidate = StorageCall("kotlinCollectionStorageCastCandidate", nullableObject, candidate, storage);
            operation.Operand = OptionalField<IBirExpression>.Of(Call(CastCandidate, nullableObject, candidate));
            Project(operation);
            return operation.Node;
        }

        // A sentinel would itself satisfy erased T=object. Guard membership
        // separately and spill the original operand so its effects execute once.
        string temp;
        do temp = "dotkt$collectionClassifier$value$" + next++; while (!names.Add(temp));
        var local = new BirLocal { Name = temp };
        var physical = operation.CopyWithOperand((IBirExpression)local.DeepClone());
        Project(physical);
        IBirExpression matches = Call(Matches, boolean, local);
        foreach (var storage in mapStorage)
            matches = new BirConditional {
                Cond = StorageCall("kotlinCollectionStorageMatches", boolean, local, storage),
                Then = OptionalField<IBirExpression>.Of(matches),
                Else = new BirConstant { Type = boolean, Value = new BirLiteral.BooleanLiteral(false) },
            };
        var target = physical.Type.OrDefault();
        var safeResult = target is TypeNode.Nullable ? target : new TypeNode.Nullable(target);
        // Nullable erasure has already run; a new T? result must use boxed
        // storage rather than merging an unboxed value with a null reference.
        safeResult = NullableGenericErasure.EraseNullableTv(safeResult, _ => false);
        var result = new BirConditional {
            Type = operation.IsTest ? default : OptionalField<TypeNode>.Of(safeResult),
            Cond = OptionalField<IBirExpression>.Of(matches), Then = OptionalField<IBirExpression>.Of((IBirExpression)physical.Node),
            Else = OptionalField<IBirExpression>.Of(operation.IsTest
                ? new BirConstant { Type = boolean, Value = new BirLiteral.BooleanLiteral(false) }
                : new BirConstant { Type = nullableObject, Value = new BirLiteral.NullLiteral() }),
        };
        return new BirValueBlock { Stmts = new IBirStatement[] {
            new BirVariable { Name = temp, Type = nullableObject, Init = OptionalField<IBirExpression>.Of(CopyExpression(operand)) },
        }, Result = result };
    }

    static TypeNode OpaqueTestType(TypeNode source) => source switch {
        TypeNode.Nullable n => new TypeNode.Nullable(OpaqueTestType(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(OpaqueTestType(o.Of)),
        _ => new TypeNode.Fqn("kotlin.Any"),
    };
}
