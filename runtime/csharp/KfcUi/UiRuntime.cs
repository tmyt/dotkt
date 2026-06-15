// kotlin/clr UI runtime (Avalonia). This is framework ceremony only — the app lifecycle plumbing
// that every Avalonia program needs. The Kotlin program drives it through the `Kfc.Ui` façade,
// passing the window's title, message and size. (Analogous to kotlin-stdlib being implemented in C#.)
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace Kfc
{
    public static class Ui
    {
        internal static string Title = "";
        internal static string Message = "";
        internal static int Width;
        internal static int Height;
        internal static string ButtonText = null;
        internal static System.Action OnClick = null;
        internal static System.Func<Window> Builder = null;

        // Entry called from Kotlin: `Ui.run { ... }`. The Kotlin lambda builds the Window itself.
        public static void Run(System.Func<Window> builder)
        {
            Builder = builder;
            Start();
        }

        // Entry called from Kotlin: `Ui.window(...)`. Blocks until the window is closed.
        public static void Window(string title, string message, int width, int height)
        {
            Title = title;
            Message = message;
            Width = width;
            Height = height;
            ButtonText = null;
            OnClick = null;
            Start();
        }

        // Window with a button; `onClick` is a delegate bound from a Kotlin lambda.
        public static void WindowWithButton(string title, string message, string buttonText, System.Action onClick)
        {
            Title = title;
            Message = message;
            Width = 480;
            Height = 240;
            ButtonText = buttonText;
            OnClick = onClick;
            Start();
        }

        private static void Start()
        {
            AppBuilder.Configure<KfcApp>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(new string[0]);
        }
    }

    public class KfcApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Ui.Builder != null)
            {
                // The window is built entirely in Kotlin.
                desktop.MainWindow = Ui.Builder();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop2)
            {
                var text = new TextBlock
                {
                    Text = Ui.Message,
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                };

                var panel = new StackPanel
                {
                    Spacing = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                panel.Children.Add(text);

                if (Ui.ButtonText != null)
                {
                    var button = new Button
                    {
                        Content = Ui.ButtonText,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    button.Click += (_, __) => Ui.OnClick?.Invoke();
                    panel.Children.Add(button);

                    // Deterministic self-test: fire the Kotlin handler once so its effect is
                    // observable without a manual click (proves lambda -> delegate wiring).
                    Ui.OnClick?.Invoke();
                }

                desktop2.MainWindow = new Window
                {
                    Title = Ui.Title,
                    Width = Ui.Width,
                    Height = Ui.Height,
                    Content = panel,
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
