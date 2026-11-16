using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedMemberOwnerBinding
{
    internal static void SourceProjectionSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var variable = new TypeNode.Tv("type", 0);
        var sourceOwner = new TypeNode.Fqn("probe.Base", new TypeNode[] { variable });
        var receiverOwner = new TypeNode.Fqn("probe.Derived", new TypeNode[] { text });
        var expectedOwner = new TypeNode.Fqn(sourceOwner.Name, new TypeNode[] { text });
        BirLocal Receiver() => new() { Name = "receiver", Sty = receiverOwner };
        var signature = new TypeNode[] { variable };
        var direct = new BirCallInstance { OwnerType = sourceOwner, Method = "read", Recv = Receiver(),
            Sig = signature, Ret = variable, Virtual = true, Args = Array.Empty<IBirExpression>() };
        var bound = new BirNewBoundDelegate { OwnerType = sourceOwner, CalleeOwner = receiverOwner, Method = "read", Recv = Receiver(),
            Sig = signature, Virtual = true };
        var native = new BirNativeInstance { Type = sourceOwner, Method = "read", Recv = Receiver(), Ret = variable, Dispatch = "virtual" };
        var property = new BirPropertyGet { Type = sourceOwner, Recv = Receiver(), Ret = variable, Member = "read" };
        var setter = new BirPropertySet { Type = sourceOwner, Recv = Receiver(), Member = "write" };
        var add = new BirEventAdd { Type = sourceOwner, Recv = Receiver(), Event = "changed" };
        var remove = new BirEventRemove { Type = sourceOwner, Recv = Receiver(), Event = "changed" };
        var clrDelegate = new BirBoundClrDelegate { ClrType = sourceOwner, Recv = Receiver(), Method = "read" };
        var fixedCall = direct with { ClrOwnerResolved = true };
        var unstamped = direct with { Recv = new BirCallStatic { Ret = receiverOwner } };
        var expressions = new IBirExpression[] { direct, bound, native, property, setter, add, remove, clrDelegate, fixedCall, unstamped };
        var bareThis = new BirCallInstance { OwnerType = new TypeNode.Fqn(sourceOwner.Name), Method = "read",
            Recv = new BirThis(), Super = true, Virtual = true, Sig = signature, Ret = variable, Args = Array.Empty<IBirExpression>() };
        var derived = new BirClassDefinition { Name = receiverOwner.Name, Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Base = sourceOwner, Methods = new[] { new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(),
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = bareThis } } } } };
        var consumer = new BirFileDeclaration { FileClass = "probe.Consumer", Methods = new[] { new BirFunctionDeclaration {
            Name = "calls", Params = Array.Empty<BirParameter>(),
            Body = expressions.Select(expression => (IBirStatement)new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expression) }).ToArray(),
        } } };
        var definitions = new BirFileDeclaration { FileClass = "probe.Definitions", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = sourceOwner.Name, Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Methods = Array.Empty<BirFunctionDeclaration>() }, derived,
        } };
        var references = ReferenceMetadataIndex.Build(Array.Empty<string>());
        ProjectOwners(new[] { consumer, definitions }, references);
        if (direct.OwnerType.Value != expectedOwner || bound.OwnerType.Value != expectedOwner || bound.CalleeOwner.Value != expectedOwner
            || native.Type.Value != expectedOwner || property.Type.Value != expectedOwner || setter.Type.Value != expectedOwner
            || add.Type.Value != expectedOwner || remove.Type.Value != expectedOwner || clrDelegate.ClrType.Value != expectedOwner
            || fixedCall.OwnerType.Value != sourceOwner || unstamped.OwnerType.Value != sourceOwner
            || bareThis.OwnerType.Value != sourceOwner || !bareThis.Virtual.Value || direct.Ret.Value != variable
            || !ReferenceEquals(signature, direct.Sig.Value) || native.Ret.Value != variable || native.Dispatch.Value != "virtual")
            throw new InvalidOperationException("Source owner projection changed frame, descriptor, dispatch, result, or exact-owner ownership");

        var shape = new Dictionary<string, TypeShape>(StringComparer.Ordinal) {
            ["probe.Contract"] = new() { Name = "probe.Contract", Kind = "interface", TypeParamCount = 1 },
            ["probe.NativeDiamond"] = new() { Name = "probe.NativeDiamond", Interfaces = new[] {
                new TypeNode.Fqn("probe.Contract", new TypeNode[] { text }),
                new TypeNode.Fqn("probe.Contract", new TypeNode[] { new TypeNode.Fqn("kotlin.Int") }),
            } },
            ["probe.Inner"] = new() { Name = "probe.Inner", TypeParamCount = 2,
                Base = new TypeNode.Fqn("probe.Contract", new TypeNode[] { variable }) },
        };
        if (ProjectUseOwner(new TypeNode.Fqn("probe.Contract"), new TypeNode.Fqn("probe.NativeDiamond"), false, false, null,
                shape, references, true) != null
            || ProjectUseOwner(new TypeNode.Fqn("probe.Contract"), new TypeNode.Fqn("probe.Inner", new TypeNode[] { text, variable }),
                false, false, null, shape, references, true) != new TypeNode.Fqn("probe.Contract", new TypeNode[] { text }))
            throw new InvalidOperationException("Source owner projection guessed an ambiguous owner or permuted own-first source arguments");
        Console.WriteLine("[source owner projection] self-test OK (flat module, unique hierarchy, own-first frames, exact slots, no late binding)");
    }
}
