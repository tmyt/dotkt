namespace Ext {
    // Stands in for any third-party / framework assembly the user references (Avalonia, WPF, NuGet…).
    public class Widget {
        public Widget(string name) { Name = name; }
        public string Name { get; }
        public bool? Enabled { get; set; }             // a nullable VALUE type (bool?) — like WinUI CheckBox.IsChecked
        public int Add(int a, int b) => a + b;
        public event System.Action<int> Changed;      // a real .NET event
        public void Fire(int n) { Changed?.Invoke(n); }
    }
}
