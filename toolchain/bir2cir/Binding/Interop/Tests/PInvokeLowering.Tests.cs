using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PInvokeLowering
{
    internal static void SelfTest()
    {
        TypeNode integer = new TypeNode.Fqn("System.Int32"), text = new TypeNode.Fqn("System.String");
        var ctor = BirMemberReference.FromPhysical(new MemberRefNode(MemberRefNode.Kinds.Ctor,
            typeof(System.Runtime.InteropServices.DllImportAttribute).Assembly.GetName().Name,
            new TypeNode.Fqn(DllImport), ".ctor", 0, new TypeNode.Fqn("System.Void"),
            MemberRefNode.Instance, new[] { text }));
        var import = new BirAttribute {
            Attr = new TypeNode.Fqn(DllImport), AttrExternal = true, MemberRef = ctor, ArgTypes = new[] { text },
            Args = new IBirAttributeValue[] { new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("native") } },
            NamedArgs = new[] {
                new BirAttributeNamedArgument { Kind = "field", Name = "CharSet",
                    Type = new TypeNode.Fqn("System.Runtime.InteropServices.CharSet"),
                    Value = new BirEnumValue { Type = new TypeNode.Fqn("System.Runtime.InteropServices.CharSet"),
                        Entry = "None", Ordinal = 0, PhysicalValue = "1" } },
                new BirAttributeNamedArgument { Kind = "field", Name = "PreserveSig",
                    Type = new TypeNode.Fqn("System.Boolean"),
                    Value = new BirConstant { Type = new TypeNode.Fqn("System.Boolean"),
                        Value = new BirLiteral.BooleanLiteral(false) } },
            },
        };
        var retained = new BirAttribute { Attr = new TypeNode.Fqn("System.ObsoleteAttribute"),
            AttrExternal = true, ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirAttributeValue>(),
            MemberRef = BirMemberReference.FromPhysical(new MemberRefNode(MemberRefNode.Kinds.Ctor,
                typeof(ObsoleteAttribute).Assembly.GetName().Name, new TypeNode.Fqn("System.ObsoleteAttribute"),
                ".ctor", 0, new TypeNode.Fqn("System.Void"), MemberRefNode.Instance, Array.Empty<TypeNode>())),
        };
        var method = new BirFunctionDeclaration {
            Name = "native", Static = true, Ret = integer, Mods = new BirModifiers { External = true },
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.ByRef(integer) },
                new BirParameter { Name = "mode", Type = new TypeNode.Fqn("probe.Mode") } },
            Attrs = new[] { retained, import },
        };
        var withFlags = method with {
            Mods = new BirModifiers { External = true, Inline = false, Infix = OptionalField<bool>.Null },
        };
        var owner = new BirClassDefinition { Name = "probe.Nested", NestedIn = "probe.Owner", Methods = new[] { withFlags } };
        Apply(new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] { owner } },
            refs: null, localEnums: new HashSet<string>(StringComparer.Ordinal) { "probe.Mode" });
        foreach (var lowered in new[] { method, withFlags })
        {
            var descriptor = lowered.Pinvoke.Value;
            if (!lowered.Extern.OrDefault() || !lowered.Body.HasValue || lowered.Body.Value.Length != 0
                || descriptor.Module.Value != "native" || descriptor.EntryPoint.Value != "native"
                || descriptor.CallingConvention.Value != "winapi" || descriptor.CharSet.Value != "none"
                || descriptor.PreserveSig.Value || !descriptor.PseudoFields.Value.SequenceEqual(new[] { "CallingConvention", "PreserveSig" })
                || descriptor.AttributeCtorRef.Value.DeclaringType.Value != ctor.DeclaringType.Value
                || descriptor.AttributeCtorRef.Value.Assembly.Value != ctor.Assembly.Value
                || descriptor.AttributeCtorRef.Value.Kind.Value != ctor.Kind.Value
                || descriptor.AttributeCtorRef.Value.Name.Value != ctor.Name.Value
                || descriptor.AttributeCtorRef.Value.GenericArity.Value != ctor.GenericArity.Value
                || descriptor.AttributeCtorRef.Value.ReturnType.Value != ctor.ReturnType.Value
                || descriptor.AttributeCtorRef.Value.CallingConvention.Value != ctor.CallingConvention.Value
                || !descriptor.AttributeCtorRef.Value.ParameterTypes.Value.SequenceEqual(ctor.ParameterTypes.Value)
                || ReferenceEquals(descriptor.AttributeCtorRef.Value, ctor)
                || ReferenceEquals(descriptor.AttributeCtorRef.Value.ParameterTypes.Value, ctor.ParameterTypes.Value)
                || lowered.Attrs.Value.Length != 1 || !ReferenceEquals(lowered.Attrs.Value[0], retained))
                throw new InvalidOperationException("Typed P/Invoke lost physical descriptor, exact ctor, or retained attributes");
        }
        if (method.Mods.IsPresent || withFlags.Mods.Value.External.IsPresent
            || !withFlags.Mods.Value.Inline.HasValue || withFlags.Mods.Value.Inline.Value || !withFlags.Mods.Value.Infix.IsNull)
            throw new InvalidOperationException("Typed P/Invoke changed absent, explicit false, or null modifier presence");
        if (FieldLegality.PosPrefix("probe/Native.kt", 7) != "Native.kt:7: "
            || FieldLegality.PosPrefix("probe/Native.kt", -1) != "Native.kt: " || FieldLegality.PosPrefix(null, 7) != "")
            throw new InvalidOperationException("Typed declaration position formatting changed");
        Console.WriteLine("[typed P/Invoke] self-test OK (flat/nested declarations, native refs/enums, descriptor ownership, presence)");
    }
}
