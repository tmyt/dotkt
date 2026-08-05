// Public STATIC members of a normal projected class remain direct KLIB static declarations
// (`App.start` / `App.Count`); dll2klib does not invent a companion type or singleton.
// Covers a static method w/ delegate arg, a static property, a static readonly FIELD (ldsfld), and a const. Own ns.
namespace Injstatic {
    public delegate void InitCb(int p);
    public class App {                                  // a NORMAL class (has instance members) with STATIC members
        public int inst = 1;
        public void run() { }
        public static int start(InitCb cb) { cb(42); return 0; }   // static method w/ delegate arg
        public static int Count => 7;                              // static property
        public static readonly int Answer = 99;                    // static FIELD (surfaced as `sprop` -> ldsfld)
        public const int Magic = 123;                              // const (literal) FIELD -> inlined value, no ldsfld
        public static int Mutable = 5;                             // mutable static FIELD -> stsfld
    }

    // A static field on a generic CLR owner still requires a constructed TypeSpec in CIL. Kotlin's direct
    // static-member surface omits the enclosing type argument, so bir2cir binds the representative object close.
    public class GenericApp<T> {
        public static int Mutable = 7;
    }
}
