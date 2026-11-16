using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExternalGenericConstraintValidation
{
    static void SourcePreparationNewConstraint<T>() where T : new() { }

    internal static void SourcePreparationSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Constraint snapshot: " + message); }
        BirConstructor Empty(string visibility = "public") => new() { Vis = visibility, Params = Array.Empty<BirParameter>() };
        BirClassDefinition Class(string name, BirConstructor constructor) => new() { Name = name, Kind = "class", Ctors = new[] { constructor } };
        var topAbstract = Class("Snapshot.TopAbstract", Empty()); topAbstract.Abstract = true;
        var semanticAbstract = Class("Snapshot.SemanticAbstract", Empty()); semanticAbstract.Mods = new BirModifiers { Abstract = true };
        var presentEmpty = Class("Snapshot.PresentEmpty", Empty());
        var nonempty = Class("Snapshot.Nonempty", new BirConstructor { Vis = "public", Params = new[] {
            new BirParameter { Name = "value", Type = new TypeNode.Fqn("System.Int32") } } });
        var privateEmpty = Class("Snapshot.PrivateEmpty", Empty("private"));
        var noConstructor = new BirClassDefinition { Name = "Snapshot.NoConstructor", Kind = "class" };
        var earlier = Class("Snapshot.Duplicate", Empty()); earlier.Abstract = true;
        var later = Class("Snapshot.Duplicate", Empty());
        var enumeration = new BirEnumDefinition { Name = "Snapshot.Enum", Kind = "enum" };
        var structure = new BirClassDefinition { Name = "Snapshot.Struct", Kind = "struct" };
        var roots = new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            topAbstract, semanticAbstract, presentEmpty, nonempty, privateEmpty, noConstructor, earlier, enumeration, structure } },
            new BirFileDeclaration { Types = new IBirTypeDeclaration[] { later } } };
        var facts = CollectLocalTypes(roots);
        Check(facts["Snapshot.TopAbstract"].Abstract && !facts["Snapshot.TopAbstract"].PublicDefaultConstructor
            && !facts["Snapshot.SemanticAbstract"].Abstract && facts["Snapshot.SemanticAbstract"].PublicDefaultConstructor,
            "physical Abstract was confused with semantic Mods.Abstract");
        Check(facts["Snapshot.PresentEmpty"].PublicDefaultConstructor && !facts["Snapshot.Nonempty"].PublicDefaultConstructor
            && !facts["Snapshot.PrivateEmpty"].PublicDefaultConstructor && !facts["Snapshot.NoConstructor"].PublicDefaultConstructor
            && facts["Snapshot.Enum"].Kind == "enum" && facts["Snapshot.Enum"].PublicDefaultConstructor
            && facts["Snapshot.Struct"].PublicDefaultConstructor && !facts["Snapshot.Duplicate"].Abstract,
            "actual constructor presence/visibility, value kind or last-owner selection changed");
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var enums = new HashSet<string>(StringComparer.Ordinal) { "Snapshot.Enum" };
        ValueTypeOracle isValue = type => type.Name is "Snapshot.Enum" or "Snapshot.Struct" || refs.IsValueType(type);
        var prepared = Prepare(roots, refs, isValue, enums);
        // This real C# MethodDef supplies the selected new() row. The consumer
        // exercises Prepared's actual local-type table, not a test-only getter.
        var method = typeof(ExternalGenericConstraintValidation).GetMethod(nameof(SourcePreparationNewConstraint), BindingFlags.Static | BindingFlags.NonPublic);
        var member = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(refs, method,
            MemberRefNode.Kinds.Method, method.DeclaringType, Array.Empty<TypeNode>()));
        var declarations = method.GetGenericArguments().Select(ReferenceMetadataIndex.GenericParamSourceDeclaration).ToArray();
        void Validate(string name, bool accepted)
        {
            var call = new BirCallStatic { Owner = member.DeclaringType, Method = method.Name, MemberRef = member,
                Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = new TypeNode.Fqn("void"), TypeArgs = new TypeNode[] { new TypeNode.Fqn(name) },
                _resolvedMethodTypeParams = declarations };
            try { prepared.ApplyResolvedMembers(call); }
            catch (InvalidOperationException error) when (!accepted && error.Message.Contains("public parameterless constructor", StringComparison.Ordinal))
            { Check(call._resolvedMethodTypeParams.HasValue, "a failing validation consumed selected metadata"); return; }
            Check(accepted && !call._resolvedMethodTypeParams.IsPresent, "Prepared accepted a wrong constructor fact or lost the selected row");
        }
        Validate("Snapshot.PresentEmpty", true); Validate("Snapshot.SemanticAbstract", true); Validate("Snapshot.Duplicate", true);
        Validate("Snapshot.Enum", true); Validate("Snapshot.Struct", true);
        Validate("Snapshot.TopAbstract", false); Validate("Snapshot.Nonempty", false); Validate("Snapshot.PrivateEmpty", false); Validate("Snapshot.NoConstructor", false);
        later.Abstract = true; later.Ctors = Array.Empty<BirConstructor>();
        Check(!facts["Snapshot.Duplicate"].Abstract && facts["Snapshot.Duplicate"].PublicDefaultConstructor,
            "prepared scalar rows retained mutable declaration state");
        Validate("Snapshot.Duplicate", true);
        Console.WriteLine("[external-constraint preparation] self-test OK (physical facts, exact constructors, last-owner snapshot and real selected-member consumer)");
    }
}
