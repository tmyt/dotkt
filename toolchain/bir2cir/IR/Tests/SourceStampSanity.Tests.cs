using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SourceStampSanity
{
    internal static void SourceSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var call = new BirCallInstance { Method = "value", Ret = integer, Sty = text };
        var statement = new BirExpressionStatement { Expr = call };
        var method = new BirFunctionDeclaration { Name = "read", Body = new IBirStatement[] { statement },
            Pos = new BirDeclarationPosition { F = "path/Source.kt", L = 12 } };
        var root = new BirFileDeclaration { FileClass = "Stamps", Methods = new[] { method } };
        Expect("Source.kt:12: Stamps.read", "'ret'");
        call.Ret = text; call.DynRet = integer;
        Expect("Source.kt:12: Stamps.read", "'dynRet'");
        call.DynRet = OptionalField<TypeNode>.Null;
        Check(root);
        call.Ret = integer; call.Sty = OptionalField<TypeNode>.Null;
        Check(root);
        call.Sty = text;
        method.Abstract = true;
        Check(root);
        method.Abstract = false; method.Body = default;
        Check(root);
        var constructor = new BirConstructor { Body = Array.Empty<IBirStatement>(),
            PreStmts = new IBirStatement[] { statement } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Ctors = new[] { constructor } };
        root.Methods = default; root.Types = new IBirTypeDeclaration[] { owner };
        Expect("Owner..ctor", "'ret'");
        constructor.PreStmts = default; constructor.ThisArgs = new IBirExpression[] { call };
        Expect("Owner..ctor", "'ret'");
        constructor.ThisArgs = default; constructor.BaseArgs = new IBirExpression[] { call };
        Expect("Owner..ctor", "'ret'");
        constructor.Body = default;
        Check(root); // A bodiless constructor does not create a checkpoint scope.
        var field = new BirFieldDeclaration { Static = true, Init = call };
        owner.Fields = new[] { field }; owner.Pos = new BirDeclarationPosition { F = "Owner.kt" };
        Expect("Owner.kt: Owner..cctor", "'ret'");
        field.Static = false;
        Check(root);
        var iface = new BirInterfaceDefinition { Name = "Interface", Kind = "interface", Fields = new[] { field } };
        field.Static = true; root.Types = new IBirTypeDeclaration[] { iface };
        Check(root); // Interface static fields are outside the existing checkpoint.
        root.Types = default; method.Body = new IBirStatement[] { statement }; root.Methods = new[] { method };
        call.Sty = new TypeNode.Array(text); call.Ret = new TypeNode.Fqn("kotlin.Array", new TypeNode[] { text });
        Check(root);
        if (!ReferenceEquals(method.Body.Value[0], statement) || !ReferenceEquals(statement.Expr.Value, call))
            throw new InvalidOperationException("Source stamp checkpoint changed owning graph identities");
        Console.WriteLine("[owning source stamp checkpoint] self-test OK (shared relation, result slots, emitted scopes, attribution and identity)");

        void Expect(string declaration, string slot)
        {
            try { Check(root); }
            catch (IrSanityException exception) when (exception.Decl == declaration && exception.Message.Contains(slot, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Source stamp checkpoint missed " + declaration + " " + slot);
        }
    }
}
