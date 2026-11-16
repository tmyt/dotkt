using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin overloads can have identical CLR value signatures while
// their nominal parameter types or generic parameter bounds remain distinct.
// Keep the value/generic frame unchanged; state that distinction in modopt.
static partial class DeclarationSignatureIdentity
{
    internal const string AllocatedKey = "declarationSignatureAllocation";
    internal const string AllocationMetadataKey = "signatureAllocation";
    static readonly Dictionary<string, BirClassDefinition> Markers = new(StringComparer.Ordinal);
    static readonly HashSet<(bool Required, string Owner, int Arity)> ReferencedModifiers = new();
    static readonly Dictionary<string, List<(BirMethodImpl Descriptor, bool IsInterface)>> PhysicalSlots = new(StringComparer.Ordinal);
    static string MarkerOwner;

    // The physical override pass has already selected a native slot. Keep its exact descriptor in memory rather
    // than inventing a Kotlin identity or sending a physical owner frame through another semantic rewrite.
    internal static void RecordSelectedPhysicalSlot(BirFunctionDeclaration implementation, BirMethodImpl descriptor, bool isInterface)
        => RecordSelectedPhysicalSlot(implementation.DeclarationId.OrDefault(), descriptor, isInterface);

    static void RecordSelectedPhysicalSlot(string id, BirMethodImpl descriptor, bool isInterface)
    {
        if (id == null) return;
        if (!PhysicalSlots.TryGetValue(id, out var slots)) PhysicalSlots[id] = slots = new();
        if (!slots.Any(slot => slot.IsInterface == isInterface && slot.Descriptor.StructurallyEquals(descriptor)))
            slots.Add(((BirMethodImpl)descriptor.DeepClone(), isInterface));
    }

    public static BirFileDeclaration[] Capture(IEnumerable<BirFileDeclaration> roots)
    {
        Markers.Clear();
        ReferencedModifiers.Clear();
        PhysicalSlots.Clear();
        BirGraphValue Walk(BirGraphValue node, string owner)
        {
            owner = node switch
            {
                BirClassDefinition { Methods.HasValue: true, Name.HasValue: true } type => type.Name.Value,
                BirInterfaceDefinition { Methods.HasValue: true, Name.HasValue: true } type => type.Name.Value,
                BirEnumDefinition { Methods.HasValue: true, Name.HasValue: true } type => type.Name.Value,
                _ => owner,
            };
            MarkerOwner = owner;
            node = node switch
            {
                BirFunctionDeclaration method => method with {
                    Params = CaptureParameters(method.Params, method.TypeParams),
                    SignatureOverrideDeclarations = method.Params.HasValue && method.Overrides.HasValue
                        ? OverrideIds(method.Overrides.Value) : method.SignatureOverrideDeclarations,
                },
                BirConstructor constructor => constructor with { Params = CaptureParameters(constructor.Params, default) },
                BirClassDefinition type => type with { Params = CaptureParameters(type.Params, type.TypeParams) },
                BirInterfaceDefinition type => type with { Params = CaptureParameters(type.Params, type.TypeParams) },
                BirEnumDefinition type => type with { Params = CaptureParameters(type.Params, type.TypeParams) },
                BirInheritedClassMethod method => method with {
                    Params = CaptureParameters(method.Params, method.TypeParams),
                    SignatureOverrideDeclarations = method.Params.HasValue && method.Overrides.HasValue
                        ? OverrideIds(method.Overrides.Value) : method.SignatureOverrideDeclarations,
                },
                BirInheritedDefaultAccessor accessor => accessor with {
                    SignatureOverrideDeclarations = accessor.Params.HasValue && accessor.Overrides.HasValue
                        ? OverrideIds(accessor.Overrides.Value) : accessor.SignatureOverrideDeclarations,
                },
                BirInheritedDefaultMethod method => method with {
                    SignatureOverrideDeclarations = method.Params.HasValue && method.Overrides.HasValue
                        ? OverrideIds(method.Overrides.Value) : method.SignatureOverrideDeclarations,
                },
                BirClosureDefinition closure => closure with { Params = CaptureParameters(closure.Params, closure.TypeParams) },
                BirNewSuspendLambda lambda => lambda with { Params = CaptureParameters(lambda.Params, lambda.TypeParams) },
                BirInlineLambda lambda => lambda with { Params = CaptureParameters(lambda.Params, default) },
                _ => node,
            };
            return node.RewriteChildren(child => Walk(child, owner));
        }
        var rootList = roots.Select(root => (BirFileDeclaration)Walk(root, root.FileClass.OrDefault())).ToArray();
        if (rootList.Length != 0)
            rootList[0] = rootList[0] with { Types = rootList[0].Types.OrEmpty().Concat(Markers.Values).ToArray() };
        return rootList;
    }

    static string[] OverrideIds(BirOverride[] overrides) => overrides.Select(edge => edge.DeclarationId.OrDefault())
        .Where(id => id != null).ToArray();

    static TypeNode[] CaptureBounds(TypeNode type, OptionalField<BirTypeParameter[]> variables) => MethodVariables(type)
        .Distinct().OrderBy(index => index).Where(index => index >= 0 && variables.HasValue && index < variables.Value.Length)
        .SelectMany(index => variables.Value[index].Constraints.OrEmpty().Select(Encode)).ToArray();

    static OptionalField<BirParameter[]> CaptureParameters(OptionalField<BirParameter[]> parameters,
        OptionalField<BirTypeParameter[]> variables) => !parameters.HasValue ? parameters
        : parameters.Value.Select(parameter => {
            if (!parameter.Type.HasValue) return parameter;
            var nominal = Encode(parameter.Type.Value);
            var bounds = CaptureBounds(parameter.Type.Value, variables);
            return parameter with { NominalParameterSignatureIdentity = nominal,
                GenericParameterSignatureBounds = bounds.Length == 0 ? parameter.GenericParameterSignatureBounds : bounds };
        }).ToArray();

    static OptionalField<BirLambdaParameter[]> CaptureParameters(OptionalField<BirLambdaParameter[]> parameters,
        OptionalField<BirTypeParameter[]> variables) => !parameters.HasValue ? parameters
        : parameters.Value.Select(parameter => {
            if (!parameter.Type.HasValue) return parameter;
            var nominal = Encode(parameter.Type.Value);
            var bounds = CaptureBounds(parameter.Type.Value, variables);
            return parameter with { NominalParameterSignatureIdentity = nominal,
                GenericParameterSignatureBounds = bounds.Length == 0 ? parameter.GenericParameterSignatureBounds : bounds };
        }).ToArray();
    static TypeNode.Fqn SelectedLocalOwner(SupertypeGraph.Def source, string selectedOwner,
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex refs) =>
        SupertypeGraph.Reachable(source, definitions, refs).Select(edge => edge.spec)
            .Where(spec => spec.Name == selectedOwner).Distinct().Single();
    static TypeNode WithIdentityModifiers(TypeNode signature, TypeNode valueType)
    {
        if (signature is not TypeNode.Mod modifier) return valueType;
        if (valueType is TypeNode.Mod existing && existing.Req == modifier.Req && existing.M == modifier.M)
            return new TypeNode.Mod(existing.Req, existing.M, WithIdentityModifiers(modifier.Of, existing.Of));
        var underlying = WithIdentityModifiers(modifier.Of, valueType);
        return modifier.M is TypeNode.Fqn marker && (Markers.ContainsKey(marker.Name)
                || ReferencedModifiers.Contains((modifier.Req, marker.Name, marker.Args?.Length ?? 0)))
            ? new TypeNode.Mod(modifier.Req, modifier.M, underlying) : underlying;
    }
    static IEnumerable<int> MethodVariables(TypeNode type) => type switch
    {
        TypeNode.Tv { Scope: "method" } variable => new[] { variable.I },
        TypeNode.Nullable nullable => MethodVariables(nullable.Of),
        TypeNode.Oblivious oblivious => MethodVariables(oblivious.Of),
        TypeNode.Projection projection => MethodVariables(projection.Of),
        TypeNode.Fqn named => named.Args?.SelectMany(MethodVariables) ?? Enumerable.Empty<int>(),
        TypeNode.Array array => MethodVariables(array.Elem),
        TypeNode.ByRef reference => MethodVariables(reference.Of),
        TypeNode.Ptr pointer => MethodVariables(pointer.Of),
        TypeNode.Mod modifier => MethodVariables(modifier.Of),
        TypeNode.Fn function => MethodVariables(function.Ret).Concat(function.Params.SelectMany(MethodVariables))
            .Concat(MethodVariables(function.Recv)).Concat(function.Ctx?.SelectMany(MethodVariables) ?? Enumerable.Empty<int>()),
        _ => Enumerable.Empty<int>(),
    };

    static TypeNode Encode(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => Encode(nullable.Of),
        TypeNode.Oblivious oblivious => Encode(oblivious.Of),
        TypeNode.Tv variable => Marker("variable", new TypeNode[] { variable }),
        TypeNode.Fqn named => Marker("nominal:" + named.Name,
            named.Args?.Select(Encode).ToArray() ?? Array.Empty<TypeNode>()),
        TypeNode.Projection projection => Marker("projection:" + projection.Variance, new[] { Encode(projection.Of) }),
        TypeNode.Star => Marker("star", Array.Empty<TypeNode>()),
        TypeNode.Array array => Marker("array:" + array.Rank + ":" + array.SzArray, new[] { Encode(array.Elem) }),
        TypeNode.ByRef reference => Marker("byref", new[] { Encode(reference.Of) }),
        TypeNode.Ptr pointer => Marker("pointer", new[] { Encode(pointer.Of) }),
        TypeNode.Mod modifier => Marker("modifier:" + modifier.Req, new[] { Encode(modifier.M), Encode(modifier.Of) }),
        TypeNode.Fn function => Marker("function:" + function.Suspend + ":" + (function.Recv != null)
            + ":" + (function.Ctx?.Length ?? 0), new[] { Encode(function.Ret) }
                .Concat(function.Params.Select(Encode))
                .Concat(function.Recv == null ? Array.Empty<TypeNode>() : new[] { Encode(function.Recv) })
                .Concat(function.Ctx?.Select(Encode) ?? Enumerable.Empty<TypeNode>()).ToArray()),
        _ => throw new InvalidOperationException("Unknown logical signature type"),
    };

    static TypeNode Marker(string meaning, TypeNode[] arguments)
    {
        // These are local TypeDefs, not a shared ABI vocabulary. Their authoritative
        // source owner scopes the naming domain so unrelated libraries do not define
        // the same private type name. Every member of one collision family shares it.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            MarkerOwner + "|" + meaning + ":" + arguments.Length)));
        var name = "DotKt.Runtime.CompilerServices.$Signature$" + digest;
        if (!Markers.ContainsKey(name))
            Markers[name] = new BirClassDefinition {
                Kind = "class", Name = name, Vis = "private", Generated = true,
                Base = new TypeNode.Fqn("System.Object"), Mods = new BirModifiers { Sealed = true },
                TypeParams = Enumerable.Range(0, arguments.Length).Select(index =>
                    new BirTypeParameter { Name = "T" + index, SpecialConstraints = new[] { "allowsRefStruct" } }).ToArray(),
                Methods = Array.Empty<BirFunctionDeclaration>(), Fields = Array.Empty<BirFieldDeclaration>(),
            };
        return new TypeNode.Fqn(name, arguments.Length == 0 ? null : arguments);
    }


    static void SelfTestTypedCapture()
    {
        var graphTypes = typeof(BirGraphValue).Assembly.GetTypes().Where(type => typeof(BirGraphValue).IsAssignableFrom(type)).ToArray();
        // A saved executable payload contains already-captured signatures, not
        // a new source declaration owned by this pass.
        var parameterOwners = graphTypes.Where(type => !typeof(IBirExecutablePayload).IsAssignableFrom(type)
            && type.GetProperty("Params")?.PropertyType is Type parameters
            && (parameters == typeof(OptionalField<BirParameter[]>) || parameters == typeof(OptionalField<BirLambdaParameter[]>)));
        if (!parameterOwners.ToHashSet().SetEquals(new[] { typeof(BirFunctionDeclaration), typeof(BirConstructor),
                typeof(BirClassDefinition), typeof(BirInterfaceDefinition), typeof(BirEnumDefinition),
                typeof(BirInheritedClassMethod), typeof(BirClosureDefinition), typeof(BirNewSuspendLambda), typeof(BirInlineLambda) })
            || !graphTypes.Where(type => type.GetProperty("Params") != null && type.GetProperty("Overrides") != null).ToHashSet()
                .SetEquals(new[] { typeof(BirFunctionDeclaration), typeof(BirInheritedClassMethod),
                    typeof(BirInheritedDefaultMethod), typeof(BirInheritedDefaultAccessor) })
            || !graphTypes.Where(type => type.GetProperty("Name") != null && type.GetProperty("Methods") != null).ToHashSet()
                .SetEquals(new[] { typeof(BirClassDefinition), typeof(BirInterfaceDefinition), typeof(BirEnumDefinition) }))
            throw new InvalidOperationException("A typed signature capture capability changed without an explicit coverage audit");
        var methodFrame = new TypeNode.Tv("method", 0);
        var ownerFrame = new TypeNode.Tv("type", 0);
        var bytes = new byte[] { 0, 255, 0xd8, 17 };
        var parameter = new BirParameter { Name = "value", Type = methodFrame, Attrs = new[] {
            new BirAttribute { Args = new IBirAttributeValue[] { new BirOpaqueBytes { Bytes = bytes } } },
        } };
        var lambdaParameter = new BirLambdaParameter { Name = "value", Type = methodFrame };
        var variables = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { ownerFrame } } };
        var inline = new BirInlineLambda { Params = new[] { lambdaParameter } };
        var suspend = new BirNewSuspendLambda { Params = new[] { lambdaParameter }, TypeParams = variables };
        var closure = new BirNewClosure { SynthClass = new BirClosureDefinition {
            Params = new[] { lambdaParameter }, TypeParams = variables } };
        var method = new BirFunctionDeclaration { Name = "keep", DeclarationId = "selected", Params = new[] { parameter },
            TypeParams = variables, Overrides = new[] { new BirOverride { DeclarationId = "first" },
                new BirOverride(), new BirOverride { DeclarationId = "second" } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = inline },
                new BirExpressionStatement { Expr = suspend }, new BirExpressionStatement { Expr = closure } } };
        var nested = method with { Name = "nested", DeclarationId = "nested", Body = Array.Empty<IBirStatement>() };
        method = method with { Body = method.Body.Value.Concat(new IBirStatement[] {
            new BirLocalFunction { Decl = nested },
            new BirExpressionStatement { Expr = new BirDefaultCarrier { Lifted = new[] { nested } } },
        }).ToArray() };
        var constructor = new BirConstructor { Params = new[] { parameter } };
        var type = new BirClassDefinition { Name = "TypedCaptureOwner", Kind = "class",
            Methods = new[] { method }, Ctors = new[] { constructor },
            InheritedClassMethods = new[] { new BirInheritedClassMethod {
                Params = method.Params, TypeParams = variables, Overrides = method.Overrides } },
            InheritedDefaultMethods = new[] { new BirInheritedDefaultMethod {
                Params = new TypeNode[] { methodFrame }, Overrides = method.Overrides } },
            InheritedDefaultAccessors = new[] { new BirInheritedDefaultAccessor {
                Params = new TypeNode[] { methodFrame }, Overrides = method.Overrides } } };
        var source = new BirFileDeclaration { FileClass = "TypedCaptureFile", Methods = new[] { method },
            Types = new IBirTypeDeclaration[] { type,
                new BirInterfaceDefinition { Name = "TypedCaptureInterface", Kind = "interface", Methods = new[] { nested } },
                new BirEnumDefinition { Name = "TypedCaptureEnum", Kind = "enum", Methods = new[] { nested } } } };
        var captured = Capture(new[] { source })[0];
        var fileMethod = captured.Methods.Value[0];
        var ownedType = (BirClassDefinition)captured.Types.Value[0];
        var capturedMethod = ownedType.Methods.Value[0];
        var slot = capturedMethod.Params.Value[0];
        if (slot.NominalParameterSignatureIdentity.Value is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            || slot.GenericParameterSignatureBounds.Value[0] is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] }
            || capturedMethod.DeclarationId.Value != "selected"
            || !capturedMethod.SignatureOverrideDeclarations.Value.SequenceEqual(new[] { "first", "second" })
            || ((TypeNode.Fqn)fileMethod.Params.Value[0].NominalParameterSignatureIdentity.Value).Name
                == ((TypeNode.Fqn)slot.NominalParameterSignatureIdentity.Value).Name
            || ownedType.Ctors.Value[0].Params.Value[0].GenericParameterSignatureBounds.IsPresent)
            throw new InvalidOperationException("Typed signature capture changed a declaration/owner/method frame or override identity");
        var capturedInline = (BirInlineLambda)((BirExpressionStatement)capturedMethod.Body.Value[0]).Expr.Value;
        var capturedSuspend = (BirNewSuspendLambda)((BirExpressionStatement)capturedMethod.Body.Value[1]).Expr.Value;
        var capturedClosure = ((BirNewClosure)((BirExpressionStatement)capturedMethod.Body.Value[2]).Expr.Value).SynthClass.Value;
        if (!capturedInline.Params.Value[0].NominalParameterSignatureIdentity.HasValue
            || capturedInline.Params.Value[0].GenericParameterSignatureBounds.IsPresent
            || !capturedSuspend.Params.Value[0].GenericParameterSignatureBounds.HasValue
            || !capturedClosure.Params.Value[0].GenericParameterSignatureBounds.HasValue
            || source.Methods.Value[0].Params.Value[0].NominalParameterSignatureIdentity.IsPresent
            || ((BirClassDefinition)source.Types.Value[0]).Methods.Value[0].Params.Value[0].NominalParameterSignatureIdentity.IsPresent
            || ((BirInlineLambda)((BirExpressionStatement)source.Methods.Value[0].Body.Value[0]).Expr.Value)
                .Params.Value[0].NominalParameterSignatureIdentity.IsPresent
            || !ReferenceEquals(source.Types.Value[0], type) || ReferenceEquals(method.Params.Value, capturedMethod.Params.Value)
            || !((BirOpaqueBytes)slot.Attrs.Value[0].Args.Value[0]).Bytes.Value.SequenceEqual(bytes)
            || captured.Types.Value.Skip(3).OfType<BirClassDefinition>().Any(marker =>
                marker.Vis.Value != "private" || !marker.Generated.Value || !marker.Mods.Value.Sealed.Value
                || marker.TypeParams.Value.Any(variable => !variable.SpecialConstraints.Value.SequenceEqual(new[] { "allowsRefStruct" }))))
            throw new InvalidOperationException("Typed signature capture lost a lambda/constructor edge, source ownership or frozen metadata");
        if (!ownedType.InheritedClassMethods.Value[0].SignatureOverrideDeclarations.Value.SequenceEqual(new[] { "first", "second" })
            || !ownedType.InheritedClassMethods.Value[0].Params.Value[0].GenericParameterSignatureBounds.HasValue
            || !ownedType.InheritedDefaultMethods.Value[0].SignatureOverrideDeclarations.Value.SequenceEqual(new[] { "first", "second" })
            || !ownedType.InheritedDefaultAccessors.Value[0].SignatureOverrideDeclarations.Value.SequenceEqual(new[] { "first", "second" })
            || !((BirLocalFunction)capturedMethod.Body.Value[3]).Decl.Value.Params.Value[0].NominalParameterSignatureIdentity.HasValue
            || !((BirDefaultCarrier)((BirExpressionStatement)capturedMethod.Body.Value[4]).Expr.Value).Lifted.Value[0]
                .Params.Value[0].GenericParameterSignatureBounds.HasValue)
            throw new InvalidOperationException("Typed signature capture missed an inherited declaration or nested-function edge");
        var nominalNames = new[] { slot.NominalParameterSignatureIdentity.Value,
            ((BirInterfaceDefinition)captured.Types.Value[1]).Methods.Value[0].Params.Value[0].NominalParameterSignatureIdentity.Value,
            ((BirEnumDefinition)captured.Types.Value[2]).Methods.Value[0].Params.Value[0].NominalParameterSignatureIdentity.Value,
        }.Cast<TypeNode.Fqn>().Select(marker => marker.Name).ToArray();
        var markerNames = captured.Types.Value.Skip(3).OfType<BirClassDefinition>().Select(marker => marker.Name.Value).ToHashSet();
        var references = new HashSet<string>();
        void MarkerReferences(TypeNode value)
        {
            if (value is not TypeNode.Fqn marker) return;
            references.Add(marker.Name);
            foreach (var argument in marker.Args ?? Array.Empty<TypeNode>()) MarkerReferences(argument);
        }
        captured.Rewrite(value => {
            if (value is BirParameter capturedParameter)
            {
                if (capturedParameter.NominalParameterSignatureIdentity.HasValue) MarkerReferences(capturedParameter.NominalParameterSignatureIdentity.Value);
                foreach (var bound in capturedParameter.GenericParameterSignatureBounds.OrEmpty()) MarkerReferences(bound);
            }
            if (value is BirLambdaParameter parameterLambda)
            {
                if (parameterLambda.NominalParameterSignatureIdentity.HasValue) MarkerReferences(parameterLambda.NominalParameterSignatureIdentity.Value);
                foreach (var bound in parameterLambda.GenericParameterSignatureBounds.OrEmpty()) MarkerReferences(bound);
            }
            return value;
        });
        if (nominalNames.Distinct().Count() != 3 || markerNames.Count == 0 || !references.IsSubsetOf(markerNames))
            throw new InvalidOperationException("Typed signature capture omitted a marker TypeDef or mixed declaration owners");
        Console.WriteLine("[typed signature capture] self-test OK (owner/method frames, bounds, overrides, lambda families, frozen metadata)");
    }

    public static void SelfTest()
    {
        SourceWritersSelfTest();
        SelfTestTypedCapture();
        SourceSelfTest();
    }
}
