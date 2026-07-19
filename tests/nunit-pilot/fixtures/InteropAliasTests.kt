// .NET-interop battery (aliased import) — migrates cases/il-alias. `import X as Y` injects the .NET type
// AND binds the alias (the PSI import scan strips the alias to the canonical FQN for facadegen). No custom
// runtime.cs: this is a pure BCL-interop case over `import System.X`, the same façade-free path dotkt.sh uses.
import System.Text.StringBuilder as SB
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

class InteropAliasTests {
    @TestAttribute
    fun aliasedBclStringBuilder() {
        val sb = SB()
        sb.Append("hello")
        sb.Append(", ")
        sb.Append("alias")
        ClassicAssert.AreEqual("hello, alias", sb.ToString())
        ClassicAssert.AreEqual(12, sb.Length)
    }
}
