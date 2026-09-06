using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace StutterDiag.Gui.Services;

/// <summary>
/// Minimal, dependency-free transient notification shown at the bottom-right of the primary
/// screen for a few seconds. Used for the Gaming-Mode "USER MARKED STUTTER" confirmation.
/// Non-activating so it never steals focus from a full-screen game.
/// </summary>
public sealed class ToastService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3.5);

    public void Show(string title, string message)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null) return;

        if (app.Dispatcher.CheckAccess())
            ShowCore(title, message);
        else
            app.Dispatcher.BeginInvoke(new Action(() => ShowCore(title, message)));
    }

    private static void ShowCore(string title, string message)
    {
        try
        {
            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = Brushes.White
            });
            panel.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                TextWrapping = TextWrapping.Wrap
            });

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                BorderThickness = new Thickness(1),
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Focusable = false,
                ShowActivated = false,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Child = panel
                }
            };

            var area = SystemParameters.WorkArea;
            window.Loaded += (_, _) =>
            {
                window.Left = area.Right - window.ActualWidth - 16;
                window.Top = area.Bottom - window.ActualHeight - 16;
            };

            var timer = new DispatcherTimer { Interval = Lifetime };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { window.Close(); } catch { /* already closed */ }
            };

            window.Show();
            timer.Start();
        }
        catch (Exception ex)
        {
            GuiLog.Error("Toast failed", ex);
        }
    }
}
