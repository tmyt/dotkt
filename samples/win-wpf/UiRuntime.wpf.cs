// kotlin/clr UI runtime — WPF variant. WINDOWS-ONLY (WPF runtime is not available on Linux).
// Build/run on Windows: `dotnet run` from this folder. Same Kotlin source as the Avalonia sample;
// only the C# UI runtime differs, because the `Kfc.Ui` façade is the seam.
using System.Windows;
using System.Windows.Controls;

namespace Kfc
{
    public static class Ui
    {
        // Entry called from Kotlin: `Ui.window(...)`. Blocks until the window is closed.
        public static void Window(string title, string message, int width, int height)
        {
            var app = new Application();
            var window = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                Content = new TextBlock
                {
                    Text = message,
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                },
            };
            app.Run(window);
        }
    }
}
