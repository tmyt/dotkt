using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void SelfTestForeignStamps()
    {
        var boolean = new TypeNode.Fqn("System.Boolean");
        var unit = new TypeNode.Fqn("void");
        BirMemberReference Reference() => new() {
            Kind = "method", Assembly = "Probe", DeclaringType = new TypeNode.Fqn("ActualOwner"), Name = "actual",
            ParameterTypes = Array.Empty<TypeNode>(), ReturnType = boolean,
        };
        BirFileDeclaration Root(params IBirExpression[] expressions) => new() {
            FileClass = "StampProbe", Methods = new[] { new BirFunctionDeclaration {
                Name = "run", Params = Array.Empty<BirParameter>(), Ret = unit,
                Body = expressions.Select(expression => (IBirStatement)new BirExpressionStatement {
                    Expr = OptionalField<IBirExpression>.Of(expression),
                }).ToArray(),
            } },
        };
        void Reject(IBirExpression expression, string kind)
        {
            try { CheckStamped(Root(expression), "stamp.kt"); }
            catch (InvalidOperationException error)
            {
                if (error.Message.StartsWith($"bir2cir: stamp.kt: a '{kind}' node resolved against a .NET member carries no declared ", StringComparison.Ordinal)) return;
                throw;
            }
            throw new InvalidOperationException("A resolved foreign declaration lost its required authored return stamp");
        }
        var local = new BirCallStatic { Method = "local", DotktDeclarationIdentityBound = false, MemberRef = Reference() };
        var nullParameterStamp = new BirFieldGet { Name = "local", ResolvedMemberParams = OptionalField<TypeNode[]>.Null };
        var localEvent = new BirEventAdd();
        var resolvedEvent = new BirEventRemove { MemberRef = Reference(), ResolvedMemberReturn = unit };
        var complete = new BirNativeStatic { Method = "external", Type = new TypeNode.Fqn("DeclaredOwner"), MemberRef = Reference(), ResolvedMemberReturn = unit };
        CheckStamped(Root(local, nullParameterStamp, localEvent, resolvedEvent, complete), "stamp.kt");
        if (local.DotktDeclarationIdentityBound.IsPresent || !ReferenceEquals(complete.ResolvedMemberReturn.Value, unit))
            throw new InvalidOperationException("Stamp validation changed an authored return or retained a consumed identity-binding marker");
        Reject(new BirNativeStatic { Method = "external", MemberRef = Reference() }, "clrStatic");
        Reject(new BirFieldGet { Name = "external", ResolvedMemberParams = Array.Empty<TypeNode>(), MemberRef = Reference() }, "field");
        Reject(new BirEventAdd { MemberRef = Reference(), ResolvedMemberReturn = OptionalField<TypeNode>.Null }, "clrEventAdd");
        if (ForeignDeclarationFacts.Read(nullParameterStamp).HasParameterStamp
            || ForeignDeclarationFacts.Read(complete).DeclaredReturn != unit)
            throw new InvalidOperationException("Foreign declaration facts confused explicit null with a stamp or reconstructed a memberRef return");
        var bound = new BirBoundClrDelegate {
            Method = "selected", Type = new TypeNode.Fqn("DiagnosticType"), ClrType = new TypeNode.Fqn("OtherType"), MemberRef = Reference(),
        };
        var facts = ForeignDeclarationFacts.Read(bound);
        if (facts.Member != "selected" || facts.Owner != "DiagnosticType" || facts.DeclaredReturn != null)
            throw new InvalidOperationException("Foreign diagnostic selection changed its authored type/name precedence");
        foreach (var node in new BirGraphValue[] { new BirAttribute { MemberRef = Reference() }, new BirMethodImpl { MemberRef = Reference() } })
        {
            var decoration = ForeignDeclarationFacts.Read(node);
            if (decoration.MemberRef == null || decoration.Kind != null || decoration.Member != ".ctor"
                || decoration.Owner != "<unknown>" || decoration.DeclaredReturn != null)
                throw new InvalidOperationException("Foreign parameter validation lost an attribute/MethodImpl reference or inferred its diagnostic identity");
        }
        ForeignDeclarationFacts.ClearDeclaredReturn(complete);
        if (complete.ResolvedMemberReturn.IsPresent || complete.MemberRef.Value.ReturnType.Value != boolean)
            throw new InvalidOperationException("Foreign return consumption changed the selected member reference");

        SourceTypeVector Signature() => SourceTypeVector.ReadFrozenText("""[{"t":"tv","scope":"method","i":0}]""");
        var constrained = new BirConstrainedCall { _foreignDeclarationSignature = Signature(), DotktDeclarationIdentityBound = true };
        var generic = new BirGenericInstance { _foreignDeclarationSignature = Signature(), ResolvedMemberReturn = unit };
        var instance = new BirNativeInstance { _foreignDeclarationSignature = Signature(), ResolvedMemberReturn = unit };
        var nativeStatic = new BirNativeStatic { _foreignDeclarationSignature = Signature(), ResolvedMemberReturn = unit };
        var getter = new BirPropertyGet { _foreignDeclarationSignature = Signature(), ResolvedMemberReturn = unit };
        var setter = new BirPropertySet { _foreignDeclarationSignature = Signature(), ResolvedMemberReturn = unit };
        var graph = Root(constrained, generic, instance, nativeStatic, getter, setter);
        ForeignStarProjectionBinding.DropDeclarationSignatures(graph);
        CheckStamped(graph, "stamp.kt");
        if (constrained._foreignDeclarationSignature.IsPresent || generic._foreignDeclarationSignature.IsPresent
            || instance._foreignDeclarationSignature.IsPresent || nativeStatic._foreignDeclarationSignature.IsPresent
            || getter._foreignDeclarationSignature.IsPresent || setter._foreignDeclarationSignature.IsPresent
            || constrained.DotktDeclarationIdentityBound.IsPresent || getter.ResolvedMemberReturn.Value != unit)
            throw new InvalidOperationException("Foreign boundary cleanup retained a current transient fact or changed an authored declaration stamp");
        Console.WriteLine("[foreign declaration stamps] self-test OK (authored presence, local/reference event, exact diagnostics, current fact cleanup)");
    }
}
