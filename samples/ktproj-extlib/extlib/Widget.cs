namespace Ext {
    // Stands in for any third-party / framework assembly the user references (Avalonia, WPF, NuGet…).
    public class Widget {
        public Widget(string name) { Name = name; }
        public string Name { get; }
        public int Add(int a, int b) => a + b;
        public event System.Action<int> Changed;      // a real .NET event
        public void Fire(int n) { Changed?.Invoke(n); }
    }
}
