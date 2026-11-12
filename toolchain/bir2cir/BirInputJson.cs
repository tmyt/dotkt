using DotKt.Bir.Lowering;

// Input construction owns typed values. JSON is only the boundary transport;
// later lowering domains migrate from the explicit one-way frontier in Program.
static class BirInputJson
{
    public static BirFileDeclaration Read(byte[] content) => BirGraphJson.ReadFile(content);
}
