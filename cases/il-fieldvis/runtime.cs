using System.Reflection;
namespace Kfc {
    // A .NET host that reflects over a DotKt-emitted type to report a member's CLR visibility. Consumed
    // façade-free from Kotlin via `import Kfc.Refl` (facadegen import scan) as a plain instance-method call
    // (`Refl().MemberVis(obj, name)`). Under the CLR property model a Kotlin property becomes a real CLR
    // property whose ACCESSORS carry the Kotlin visibility (the backing field is uniformly assembly-internal,
    // access routed through get_/set_), so the honored private/internal/public shows up on the getter.
    public class Refl {
        public string MemberVis(object o, string name) {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var g = p?.GetGetMethod(true);
            if (g == null) return "<none>";
            return g.IsPrivate ? "Private" : g.IsAssembly ? "Internal" : g.IsFamily ? "Protected" : "Public";
        }
    }
}
