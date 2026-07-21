// Producer for #205: a GENERIC interface (`IReader<T>`) deriving a member-bearing NON-generic base interface
// (`IPingable`). facadegen's InterfaceSuperTypes used to emit ONLY generic supers, so `IReader`1` surfaced with no
// super edge and the inherited non-generic-base member `Ping` was dropped: `r.Ping()` -> unresolved reference and
// `IReader<Doc>` was not assignable to `IPingable`. The fix emits the namespace-qualified non-generic super edge so
// both hold. Own namespace so `Doc` does not collide with the other cases' types.
namespace IfaceBaseGen {
    public interface IPingable { string Ping(); }
    public interface IReader<T> : IPingable { T Read(); }
    public class Doc { public string Text = ""; }
    public class DocReader : IReader<Doc> {
        public string Ping() => "pong";
        public Doc Read() => new Doc { Text = "hi" };
    }
    public class Source { public IReader<Doc> Reader => new DocReader(); }
}
