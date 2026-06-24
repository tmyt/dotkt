namespace Aaa { public class Args { public Args(){} } }
namespace Bbb { public class Args { public Bbb.Args self() => this; } }   // SAME simple name, other namespace
namespace App {
    public class Base { public Base(){} public virtual int handle(Aaa.Args x) => 1; public Bbb.Args other() => new Bbb.Args(); }
}
