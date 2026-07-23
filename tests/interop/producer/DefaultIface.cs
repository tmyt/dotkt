// Regression producer for CLR default interface implementations (DIM). A Kotlin implementation must provide only
// Required; facadegen must preserve the concrete/default nature of Offset, Add, and Echo instead of surfacing every
// interface member as an abstract implementation obligation.
namespace DefaultIface {
    public interface IDefaultArithmetic {
        int Offset => 10;
        int Add(int value) => Offset + value;
        U Echo<U>(U value) => value;
        int Required(int value);
    }
}
