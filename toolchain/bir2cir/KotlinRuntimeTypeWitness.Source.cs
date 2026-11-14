using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinRuntimeTypeWitness
{
    internal static void LowerOperations(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        var names = ClassifierNames(root);
        var next = 0;
        RewriteClassifierOperations(root, operation => {
            if (!operation.Classifier.HasValue) return operation.Node;
            var classifier = CopyExpression(operation.Classifier.Value);
            operation.Classifier = default;
            var predicate = operation.Predicate.HasValue ? CopyExpression(operation.Predicate.Value)
                : throw new InvalidOperationException("Runtime classifier has no predicate descriptor");
            operation.Predicate = default;
            var flags = operation.Flags.HasValue ? CopyExpression(operation.Flags.Value)
                : throw new InvalidOperationException("Runtime classifier has no Kotlin type flags");
            string name;
            do name = "dotkt$runtimeClassifier$value$" + next++; while (!names.Add(name));
            BirLocal Local() => new() { Name = name, Sty = BirGraphValue.CopyType(NullableAny) };
            BirNativeInstance NativeCall(string owner, string method, IBirExpression receiver, string result,
                string[] parameters, params IBirExpression[] arguments) => new() {
                Type = new TypeNode.Fqn(owner), Method = method, Recv = OptionalField<IBirExpression>.Of(CopyExpression(receiver)),
                Args = arguments, ArgTypes = parameters.Select(parameter => (TypeNode)new TypeNode.Fqn(parameter)).ToArray(),
                Ret = new TypeNode.Fqn(result), Dispatch = "callvirt",
                MemberRef = BirMemberReference.FromPhysical(ClrMemberResolution.ArrayStorageMethodOf(refs, method, parameters, owner, false)),
            };
            var typeType = new TypeNode.Fqn("System.Type");
            // An opaque foreign construction uses its open definition, not
            // Object or G<Object>. Raw membership owns that token's view test.
            var test = Choose(NativeCall("System.Type", "get_IsGenericTypeDefinition", classifier, "System.Boolean", []),
                new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt"), Method = "starProjectionHasView",
                    Sig = new TypeNode[] { typeType, typeType }, Ret = Boolean, Args = new IBirExpression[] {
                        NativeCall("System.Object", "GetType", Local(), "System.Type", []), CopyExpression(classifier) } },
                NativeCall("System.Type", "IsInstanceOfType", classifier, "System.Boolean", new[] { "System.Object" }, Local()), Boolean);
            var matches = Choose(KotlinTypeWitness.BinaryValue("===", Local(), NullValue()), BoolValue(true),
                Choose(KotlinTypeWitness.BinaryValue("!=", KotlinTypeWitness.BinaryValue("&", CopyExpression(flags), KotlinTypeWitness.ConstantValue(-2)),
                        KotlinTypeWitness.ConstantValue(0)), BoolValue(true),
                    Choose(KotlinTypeWitness.BinaryValue("===", CopyExpression(predicate), NullValue()), test,
                        Choose(new BirCast { Type = Boolean, E = new BirDelegateInvoke {
                                FuncType = new TypeNode.Fn(false, new TypeNode.Fqn("System.Object"), new TypeNode[] { new TypeNode.Fqn("System.Object") }),
                                Recv = OptionalField<IBirExpression>.Of(CopyExpression(predicate)), Args = new IBirExpression[] { Local() } } },
                            (IBirExpression)test.DeepClone(), BoolValue(false), Boolean), Boolean)));
            var statements = new List<IBirStatement> { new BirVariable { Name = name, Type = NullableAny,
                Init = operation.Operand.HasValue ? OptionalField<IBirExpression>.Of(CopyExpression(operation.Operand.Value)) : OptionalField<IBirExpression>.Null } };
            var physical = operation.CopyWithOperand(Local());
            IBirExpression result;
            if (operation.IsCast)
            {
                // A semantic predicate cannot permit an impossible physical
                // array cast. The original operation still owns null rejection.
                var semanticMatches = Choose(matches,
                    Choose(KotlinTypeWitness.BinaryValue("===", Local(), NullValue()), BoolValue(true), physical.CastTest(Local()), Boolean),
                    BoolValue(false), Boolean);
                // Without a predicate retain the matches-only preguard: the
                // collection cast, not its is-test, owns membership exceptions.
                var castMatches = Choose(KotlinTypeWitness.BinaryValue("===", CopyExpression(predicate), NullValue()),
                    (IBirExpression)matches.DeepClone(), semanticMatches, Boolean);
                statements.Add(new BirIf { Branches = new[] { new BirBranch {
                    Cond = new BirUnaryOperation { Op = "!", E = castMatches }, Body = new IBirStatement[] {
                        new BirThrow { Value = new BirNew { Type = new TypeNode.Fqn("kotlin.ClassCastException"),
                            Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>() } },
                    } } } });
                result = (IBirExpression)physical.Node;
            }
            // isInstRef returns a boxed nullable value, never an unboxed T.
            else result = Choose(matches, (IBirExpression)physical.Node, operation.IsTest ? BoolValue(false) : NullValue(),
                operation.IsTest ? Boolean : new TypeNode.Fqn("object"));
            return new BirValueBlock { Stmts = statements.ToArray(), Result = OptionalField<IBirExpression>.Of(result) };
        });
    }
}
