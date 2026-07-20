// Producer source for the migrated il-injfqn case. Two `Args` types in DIFFERENT namespaces are both injected; the
// override target's param type must resolve to the EXACT one (InjfqnAaa.Args), not whichever same-simple-name type
// won the dedup, so the Kotlin override matches. Own (prefixed) namespaces.
namespace InjfqnAaa { public class Args { public Args(){} } }
namespace InjfqnBbb { public class Args { public InjfqnBbb.Args self() => this; } }   // SAME simple name, other namespace
namespace InjfqnApp {
    public class Base { public Base(){} public virtual int handle(InjfqnAaa.Args x) => 1; public InjfqnBbb.Args other() => new InjfqnBbb.Args(); }
}
