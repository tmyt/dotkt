using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExternalGenericConstraintValidation
{
    static void WholeSourceConstraintSignature<T>(T? value) where T : struct { }

    internal static void SourceSelfTest() => Prepared.WholeSourceSelfTest();

    public sealed partial class Prepared
    {
        internal static void WholeSourceSelfTest()
        {
            static void Need(bool value, string message)
            { if (!value) throw new InvalidOperationException("Owning whole constraints: " + message); }
            static void Reject(Action action)
            {
                try { action(); }
                catch (InvalidOperationException error) when (error.Message.Contains("CLR generic constraint violation", StringComparison.Ordinal)) { return; }
                throw new InvalidOperationException("Whole use-site validation accepted a violated physical TypeSpec constraint");
            }
            var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var integer = new TypeNode.Fqn("System.Int32"); var text = new TypeNode.Fqn("System.String");
            var ownerVariable = new TypeNode.Tv("type", 0); var ownVariable = new TypeNode.Tv("type", 1);
            var methodVariable = new TypeNode.Tv("method", 0);
            var nullable = typeof(Nullable<>).FullName;
            TypeNode.Fqn Application(TypeNode argument) => new(nullable, new[] { argument });
            BirTypeParameter Parameter(string name) => new() { Name = name, SpecialConstraints = new[] { "struct" } };
            var reflected = typeof(ExternalGenericConstraintValidation).GetMethod(nameof(WholeSourceConstraintSignature),
                BindingFlags.Static | BindingFlags.NonPublic);
            var member = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(references, reflected,
                MemberRefNode.Kinds.Method, reflected.DeclaringType, Array.Empty<TypeNode>()));
            var selected = reflected.GetGenericArguments().Select(ReferenceMetadataReader.GenericParamSourceDeclaration).ToArray();
            // A real referenced declaration owns !!0, not this nongeneric
            // caller. Both its MemberRef and carried selection vectors stay
            // outside the whole use-site check's caller frame.
            var selectedCall = new BirCallStatic { Owner = member.DeclaringType, Method = reflected.Name,
                MemberRef = member, TypeArgs = new TypeNode[] { integer },
                Sig = new TypeNode[] { Application(methodVariable) },
                ShapeTypes = new TypeNode[] { Application(methodVariable) },
                ArgTypes = new TypeNode[] { Application(methodVariable) },
                Args = new IBirExpression[] { new BirConstant { Type = Application(integer), Value = new BirLiteral.NullLiteral() } },
                Ret = new TypeNode.Fqn("void"), _resolvedMethodTypeParams = selected };
            var field = new BirFieldDeclaration { Name = "captured", Type = Application(ownerVariable) };
            var ownField = new BirFieldDeclaration { Name = "own", Type = Application(ownVariable) };
            var local = new BirVariable { Name = "value", Type = Application(methodVariable) };
            var genericMethod = new BirFunctionDeclaration { Name = "generic", Params = Array.Empty<BirParameter>(),
                TypeParams = new[] { Parameter("Method") }, Body = new IBirStatement[] { local } };
            var constructorLocal = new BirVariable { Name = "value", Type = Application(ownerVariable) };
            var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { constructorLocal } };
            var owner = new BirClassDefinition { Name = "ConstraintFrames.Nested", Kind = "class",
                CapturedTypeParams = new[] { Parameter("Captured") }, TypeParams = new[] { Parameter("Own") },
                Fields = new[] { field, ownField }, Methods = new[] { genericMethod }, Ctors = new[] { constructor } };
            var holder = new BirClassDefinition { Name = "NullableHolder", Kind = "class", Ctors = new[] { new BirConstructor {
                Params = new[] { new BirParameter { Name = "value", Type = Application(integer) } }, Body = Array.Empty<IBirStatement>() } } };
            var method = new BirFunctionDeclaration { Name = "caller", Params = Array.Empty<BirParameter>(),
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = selectedCall } } };
            var root = new BirFileDeclaration { FileClass = "WholeConstraints", Methods = new[] { method }, Types = new IBirTypeDeclaration[] { owner, holder } };
            var roots = new[] { root };
            var prepared = Prepare(roots, references, references.IsValueType, new HashSet<string>(StringComparer.Ordinal));
            var cache = prepared._declarationCache; var locals = prepared._localTypes;
            var methods = root.Methods.Value; var body = method.Body.Value;
            var shape = selectedCall.ShapeTypes.Value; var arguments = selectedCall.Args.Value;
            prepared.Apply(root);
            Need(cache.ContainsKey(nullable) && ReferenceEquals(prepared._declarationCache, cache)
                && ReferenceEquals(prepared._localTypes, locals) && ReferenceEquals(root.Methods.Value, methods)
                && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(selectedCall.ShapeTypes.Value, shape)
                && ReferenceEquals(selectedCall.Args.Value, arguments) && ReferenceEquals(selectedCall.MemberRef.Value, member)
                && ReferenceEquals(owner.Fields.Value[0], field) && ReferenceEquals(owner.Methods.Value[0], genericMethod)
                && ReferenceEquals(owner.Ctors.Value[0], constructor) && local.Type.Value == Application(methodVariable),
                "validation replaced owning records/types, rebuilt preparation or ignored complete captured/own/method frames");
            Need(ReferenceEquals(selectedCall._resolvedMethodTypeParams.Value, selected),
                "whole validation consumed the distinct selected-member capability");
            prepared.ApplyResolvedMembers(root);
            Need(!selectedCall._resolvedMethodTypeParams.IsPresent && ReferenceEquals(prepared._declarationCache, cache),
                "the real later selected-member validator did not continue the same Prepared/cache");
            prepared.Apply(root);

            var invalid = Application(text);
            Reject(() => prepared.Apply(new BirFieldDeclaration { Name = "invalid", Type = invalid }));
            Reject(() => prepared.Apply(new BirVariable { Name = "invalid", Type = new TypeNode.Fn(false, integer,
                new TypeNode[] { new TypeNode.Array(new TypeNode.ByRef(invalid)) }) }));

            // Actual local constructor: argTypes are caller use-site types for
            // new, unlike the open foreign vectors on the selected call above.
            var construction = new BirNew { Type = new TypeNode.Fqn("NullableHolder"),
                ArgTypes = new TypeNode[] { Application(integer) }, Args = new IBirExpression[] { new BirLocal { Name = "value" } } };
            var forConstruction = Prepare(roots, references, references.IsValueType, new HashSet<string>(StringComparer.Ordinal));
            Need(forConstruction._declarationCache.Count == 0, "new preparation was not initially empty");
            forConstruction.Apply(construction);
            Need(forConstruction._declarationCache.ContainsKey(nullable), "new.argTypes was mistaken for an excluded foreign declaration vector");

            // These typed facts serialize as frozen strings/bytes. Their types
            // were not traversed by the old whole-tree validator either.
            var frozen = new BirFunctionDeclaration { Name = "metadata", Params = new[] { new BirParameter {
                Name = "value", Type = integer, KotlinType = invalid } }, Ret = integer, RetKotlinType = invalid,
                MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { invalid } }),
                InlineBir = new byte[] { 0x80 }, Body = Array.Empty<IBirStatement>() };
            var snapshot = frozen.MethodTypeParameterBoundsPre.Value;
            prepared.Apply(frozen);
            Need(ReferenceEquals(frozen.RetKotlinType.Value, invalid) && ReferenceEquals(frozen.Params.Value[0].KotlinType.Value, invalid)
                && ReferenceEquals(frozen.MethodTypeParameterBoundsPre.Value, snapshot), "encoded source metadata was reinterpreted as a TypeSpec");
            Console.WriteLine("[owning whole generic constraints] self-test OK (complete lexical frames, actual referenced descriptors, new argTypes, same Prepared/cache and separate selected-member consumer)");
        }
    }
}
