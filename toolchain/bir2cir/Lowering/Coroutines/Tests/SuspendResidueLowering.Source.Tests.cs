using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendResidueLowering
{
    internal static void SourceSelfTest()
    {
        SourceDropModifierSelfTest();
        static void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Typed suspend residue: " + reason); }
        static BirFunctionDeclaration Method(string name) => new() { Name = name,
            Mods = new BirModifiers { Suspend = true }, Ret = new TypeNode.Fqn("kotlin.Unit"),
            SuspendResult = new TypeNode.Fqn("kotlin.Unit") };
        static BirNewSuspendLambda Lambda(string id) => new() { DeclarationId = id, Arity = 0,
            Params = Array.Empty<BirLambdaParameter>(), Captures = Array.Empty<BirCaptureDescriptor>(),
            SuspendRet = new TypeNode.Fqn("kotlin.Int"), Body = new IBirStatement[] { new BirReturn {
                Value = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(1) } } } };
        var nested = Lambda("discarded-lambda");
        var first = new BirExpressionStatement { Expr = nested };
        var second = new BirReturn();
        var top = Method("retained"); top.Body = new IBirStatement[] { first, second };
        top.Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Fqn("kotlin.Int") } };
        top.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        top.BodyTerminates = false;
        var oldBody = top.Body.Value; var oldParams = top.Params.Value; var oldFrame = top.TypeParams.Value;
        var abstractMethod = Method("abstract"); abstractMethod.Abstract = true;
        abstractMethod.Body = Array.Empty<IBirStatement>(); abstractMethod.BodyTerminates = false;
        var absentBody = Method("absent"); absentBody.BodyTerminates = false;
        var nullBody = Method("null"); nullBody.Body = OptionalField<IBirStatement[]>.Null;
        var emptyBody = Method("empty"); emptyBody.Body = Array.Empty<IBirStatement>();
        var member = Method("member"); var memberStatement = new BirReturn();
        member.Body = new IBirStatement[] { memberStatement };
        var ordinary = Method("ordinary"); ordinary.Mods.Value.Suspend = false;
        ordinary.Body = new IBirStatement[] { new BirReturn() };
        var ordinaryBody = ordinary.Body;
        var methods = new[] { abstractMethod, absentBody, nullBody, emptyBody, member, ordinary };
        var owner = new BirClassDefinition { Name = "ResidueOwner", Methods = methods };
        var file = new BirFileDeclaration { FileClass = "ResidueFile", Methods = new[] { top },
            Types = new IBirTypeDeclaration[] { owner } };
        var files = new[] { file }; var roots = file.Types.Value;
        var retired = new List<BirGraphValue>(); var seen = new List<BirGraphValue>();
        ApplyAll(files, appBuild: false, discarded => {
            Check(ReferenceEquals(top.Body.Value, oldBody)
                || ReferenceEquals(discarded, memberStatement), "a discarded forest was reported after detachment");
            if (ReferenceEquals(discarded, memberStatement))
                Check(ReferenceEquals(member.Body.Value[0], memberStatement), "member body detached before retirement");
            retired.Add(discarded); discarded.Visit(seen.Add);
        });
        Check(retired.Count == 3 && ReferenceEquals(retired[0], first) && ReferenceEquals(retired[1], second)
            && ReferenceEquals(retired[2], memberStatement) && seen.Count(node => ReferenceEquals(node, nested)) == 1,
            "discarded callable forests, owner order or callback multiplicity changed");
        Check(ReferenceEquals(files[0], file) && ReferenceEquals(file.Types.Value, roots)
            && ReferenceEquals(owner.Methods.Value, methods) && ReferenceEquals(file.Methods.Value[0], top)
            && ReferenceEquals(top.Params.Value, oldParams) && ReferenceEquals(top.TypeParams.Value, oldFrame),
            "residue replacement changed continuing declaration/vector/frame identity");
        Check(top.BodyTerminates.Value && member.BodyTerminates.Value && emptyBody.BodyTerminates.Value
            && top.Body.Value is [BirThrow { Value.Value: BirNewClr construction }]
            && construction.Type.Value is TypeNode.Fqn { Name: "System.NotSupportedException" }
            && construction.ArgTypes.Value is [TypeNode.Fqn { Name: "kotlin.String" }]
            && construction.Args.Value is [BirConstant { Value.Value: BirLiteral.TextLiteral message }]
            && message.Value.StartsWith("ResidueFile.retained:", StringComparison.Ordinal),
            "stub constructor, string slot, message owner or termination fact changed");
        Check(abstractMethod.Body.HasValue && abstractMethod.Body.Value.Length == 0 && !abstractMethod.BodyTerminates.Value
            && !absentBody.Body.IsPresent && !absentBody.BodyTerminates.Value
            && nullBody.Body.IsNull && !nullBody.BodyTerminates.IsPresent
            && emptyBody.Body.Value.Length == 1 && ordinary.Body.Equals(ordinaryBody),
            "abstract/absent/null/empty/nonsuspend distinctions were collapsed");
        RoundtripMetadata.RequireSuspendResults(files);

        // App residue is a declaration invariant, before abstract/body guards.
        var app = Method("appAbstract"); app.Abstract = true;
        var appRoot = new BirFileDeclaration { FileClass = "AppOwner", Methods = new[] { app, Method("later") } };
        var appRetired = false; var appRejected = false;
        try { ApplyAll(new[] { appRoot }, true, _ => appRetired = true); }
        catch (NotSupportedException error) { appRejected = error.Message.Contains("'AppOwner.appAbstract'", StringComparison.Ordinal); }
        Check(appRejected && !appRetired && !app.Body.IsPresent && !app.BodyTerminates.IsPresent,
            "app miss was deferred past abstract/body checks or mutated the rejected declaration");

        var logical = new TypeNode.Fn(true, new TypeNode.Nullable(new TypeNode.Tv("method", 0)),
            new TypeNode[] { new TypeNode.Fqn("kotlin.Int") });
        var valid = Method("logical"); valid.SuspendResult = logical;
        var noModifier = new BirFunctionDeclaration { Name = "ordinary", SuspendResult = OptionalField<TypeNode>.Null };
        var invalid = Method("missing"); invalid.SuspendResult = default;
        var validRoot = new BirFileDeclaration { Methods = new[] { noModifier, valid }, Types = new IBirTypeDeclaration[] {
            new BirInterfaceDefinition { Name = "LogicalOwner", Methods = new[] { invalid } } } };
        var missingRejected = false;
        try { RoundtripMetadata.RequireSuspendResults(new[] { validRoot }); }
        catch (InvalidOperationException error) { missingRejected = error.Message == "suspend declaration 'missing' did not preserve its logical result"; }
        Check(missingRejected && ReferenceEquals(valid.SuspendResult.Value, logical) && !invalid.SuspendResult.IsPresent,
            "logical result validation skipped current owner members, changed frozen frames or fabricated a fact");
        invalid.SuspendResult = OptionalField<TypeNode>.Null;
        var nullRejected = false;
        try { RoundtripMetadata.RequireSuspendResults(new[] { validRoot }); }
        catch (InvalidOperationException error) { nullRejected = error.Message.Contains("'missing'", StringComparison.Ordinal); }
        Check(nullRejected && invalid.SuspendResult.IsNull, "explicit-null logical result was accepted or rewritten");
        invalid.SuspendResult = logical;
        RoundtripMetadata.RequireSuspendResults(new[] { validRoot });
        Console.WriteLine("[typed suspend residue] self-test OK (retirement before detach, declaration identity, body presence, app guard and logical source facts)");
    }

    internal static void SourceDropModifierSelfTest()
    {
        static void Need(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Owning suspend metadata transfer: " + reason); }
        static BirFunctionDeclaration Method(string name, OptionalField<bool> suspend) => new() {
            Name = name, Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("void"),
            Body = Array.Empty<IBirStatement>(), Mods = new BirModifiers { Suspend = suspend },
        };
        var enabled = Method("enabled", true);
        var disabled = Method("disabled", false);
        var explicitNull = Method("explicitNull", OptionalField<bool>.Null);
        var missing = Method("missing", default);
        // A saved result must not be overwritten when there is no raw suspend
        // fact to transfer (for example on an already-processed declaration).
        missing.RoundtripSuspend = false;
        var absentMods = new BirFunctionDeclaration { Name = "absentMods" };
        var nullMods = new BirFunctionDeclaration { Name = "nullMods", Mods = OptionalField<BirModifiers>.Null };
        var unrelated = Method("unrelatedModifiers", true); unrelated.Mods.Value.Inline = true;
        var interfaceMethod = Method("interfaceMember", false);
        var classMethod = Method("classMember", true);
        var cls = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { classMethod } };
        var iface = new BirInterfaceDefinition { Name = "Interface", Kind = "interface", Methods = new[] { interfaceMethod } };
        var methods = new[] { enabled, disabled, explicitNull, missing, absentMods, nullMods, unrelated };
        var file = new BirFileDeclaration { FileClass = "SuspendMetadata", Methods = methods,
            Types = new IBirTypeDeclaration[] { cls, iface } };
        var typeVector = file.Types.Value;
        var actualModifiers = enabled.Mods.Value; var emptyModifiers = missing.Mods.Value;
        var memberVector = cls.Methods.Value; var body = enabled.Body.Value; var parameters = enabled.Params.Value;
        var transfers = 0;
        // Exercise the real preceding grant, not a manually fabricated pending
        // marker. There are no raw IDs here, so identity custody is not invoked.
        RoundtripMetadata.PrepareMetadata(file, runtimeMode: false,
            (_, _, _, _) => transfers++);
        Need(transfers == 0 && enabled.RoundtripMetadataPending.Value && classMethod.RoundtripMetadataPending.Value
            && interfaceMethod.RoundtripMetadataPending.Value, "actual metadata grant did not precede modifier transfer");
        var unmarked = Method("authoredAfterGrant", true);
        var falseGrant = Method("notGranted", true); falseGrant.RoundtripMetadataPending = false;
        file.Methods = file.Methods.Value.Concat(new[] { unmarked, falseGrant }).ToArray();
        var continuingMethods = file.Methods.Value;
        DropModifier(file);
        Need(enabled.RoundtripSuspend.HasValue && enabled.RoundtripSuspend.Value
            && disabled.RoundtripSuspend.HasValue && !disabled.RoundtripSuspend.Value
            && explicitNull.RoundtripSuspend.IsNull && missing.RoundtripSuspend.HasValue && !missing.RoundtripSuspend.Value,
            "true/false/null/absent suspend facts were collapsed or an absent raw fact overwrote saved metadata");
        Need(!unmarked.RoundtripSuspend.IsPresent && !falseGrant.RoundtripSuspend.IsPresent
            && !unmarked.Mods.Value.Suspend.IsPresent && !falseGrant.Mods.Value.Suspend.IsPresent
            && classMethod.RoundtripSuspend.Value && interfaceMethod.RoundtripSuspend.HasValue && !interfaceMethod.RoundtripSuspend.Value,
            "ungranted declarations acquired metadata or actual type methods were skipped");
        Need(!absentMods.Mods.IsPresent && nullMods.Mods.IsNull
            && !absentMods.RoundtripSuspend.IsPresent && !nullMods.RoundtripSuspend.IsPresent
            && ReferenceEquals(enabled.Mods.Value, actualModifiers) && !actualModifiers.Suspend.IsPresent
            && ReferenceEquals(missing.Mods.Value, emptyModifiers) && !emptyModifiers.Suspend.IsPresent
            && unrelated.Mods.Value.Inline.Value && !unrelated.Mods.Value.Suspend.IsPresent,
            "absent/null/empty mods presence, unrelated modifiers or actual modifiers ownership changed");
        Need(ReferenceEquals(file.Types.Value, typeVector) && ReferenceEquals(file.Methods.Value, continuingMethods)
            && ReferenceEquals(cls.Methods.Value, memberVector) && ReferenceEquals(enabled.Body.Value, body)
            && ReferenceEquals(enabled.Params.Value, parameters) && ReferenceEquals(file.Methods.Value[0], methods[0]),
            "modifier transfer replaced declarations, operands, signatures or owning vectors");
        DropModifier(file);
        Need(enabled.RoundtripSuspend.Value && explicitNull.RoundtripSuspend.IsNull
            && ReferenceEquals(enabled.Mods.Value, actualModifiers) && !unmarked.RoundtripSuspend.IsPresent,
            "repeated modifier transfer fabricated or lost metadata");
        Console.WriteLine("[owning suspend metadata transfer] real grant, exact scalar/modifier presence, capability and ownership: OK");
    }
}
