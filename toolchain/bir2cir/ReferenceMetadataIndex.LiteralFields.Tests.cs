using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
    internal static void LiteralFieldValueSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Typed reference literal: " + message); }
        var refs = Build(new[] { typeof(object).Assembly.Location });
        var owner = new TypeNode.Fqn("System.Int32");
        // MetadataLoadContext types retain the existing TypeNodeOf FQN here;
        // this selector does not perform the later CLR shorthand lowering.
        Require(refs.TryResolveLiteralFieldValue(owner, "MaxValue", out var type, out var literal)
            && type == new TypeNode.Fqn("System.Int32") && literal == new BirLiteral.IntegerLiteral(int.MaxValue),
            "real Constant-table selector lost the physical scalar type/value");
        var cachedCount = refs._literalFieldCache.Count;
        Require(refs.TryResolveLiteralFieldValue(owner, "MaxValue", out var repeatedType, out var repeated)
            && refs._literalFieldCache.Count == cachedCount && ReferenceEquals(literal, repeated)
            && repeatedType == type,
            "the repeated typed read did not preserve its exact selection/cache");
        Require(!refs.TryResolveLiteralFieldValue(owner, "missing-constant", out _, out _), "missing field was synthesized");
        cachedCount = refs._literalFieldCache.Count;
        Require(!refs.TryResolveLiteralFieldValue(owner, "missing-constant", out _, out _)
            && refs._literalFieldCache.Count == cachedCount, "a literal miss did not share the same cache");
        Require(refs.TryResolveLiteralFieldValue(new TypeNode.Fqn("System.UInt32"), "MaxValue", out _, out var unsigned32)
            && unsigned32 == new BirLiteral.IntegerLiteral(-1)
            && refs.TryResolveLiteralFieldValue(new TypeNode.Fqn("System.UInt64"), "MaxValue", out _, out var unsigned64)
            && unsigned64 == new BirLiteral.IntegerLiteral(-1)
            && refs.TryResolveLiteralFieldValue(new TypeNode.Fqn("System.Char"), "MaxValue", out _, out var character)
            && character == new BirLiteral.IntegerLiteral(char.MaxValue), "unsigned widths or char normalization changed");
        var nan32 = unchecked((int)0xffc00001);
        var nan64 = unchecked((long)0xfff8000000000001);
        Require(LiteralValue(-0.0f) == new Float32Literal(unchecked((int)0x80000000))
            && LiteralValue(-0.0d) == new Float64Literal(long.MinValue)
            && LiteralValue(BitConverter.Int32BitsToSingle(nan32)) == new Float32Literal(nan32)
            && LiteralValue(BitConverter.Int64BitsToDouble(nan64)) == new Float64Literal(nan64)
            && LiteralValue((sbyte)-7) == new BirLiteral.IntegerLiteral(-7)
            && LiteralValue((ushort)65535) == new BirLiteral.IntegerLiteral(65535)
            && LiteralValue(false) == new BirLiteral.BooleanLiteral(false), "raw scalar flags, signed zero or NaN bits changed");
        const string utf16 = "\ud800A\udfff";
        Require(LiteralValue(utf16) == new BirLiteral.TextLiteral(utf16)
            && LiteralText.Read(LiteralValueNode(utf16)) == utf16,
            "the shared actual leaf renderer lost UTF-16 code units");
    }
}
