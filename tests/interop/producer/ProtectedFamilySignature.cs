namespace ProtectedFamilySignature;

public class Base<T>
{
    protected class Token { public override string ToString() => "native-token"; }
    protected readonly Token Saved = new Token();
    public int Calls { get; private set; }
    protected Token MakeToken() { Calls++; return Saved; }
    protected string Describe(Token token) => token.ToString();
}
