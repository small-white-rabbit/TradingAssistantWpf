using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StockReviewWpf.Controls;

public static class NumberAnimator
{
    public static void Run(TextBlock tb, double target, string format = "F1", string suffix = "", double durationMs = 1500)
    {
        if (tb == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var duration = TimeSpan.FromMilliseconds(durationMs);

        void Tick(object? s, EventArgs e)
        {
            var elapsed = sw.Elapsed;
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - t, 3);
            var current = eased * target;
            tb.Text = current.ToString(format, CultureInfo.InvariantCulture) + suffix;
            if (t >= 1)
            {
                CompositionTarget.Rendering -= Tick;
                tb.Text = target.ToString(format, CultureInfo.InvariantCulture) + suffix;
            }
        }

        tb.Unloaded += OnUnloadedCleanup;
        CompositionTarget.Rendering += Tick;

        void OnUnloadedCleanup(object? sender, RoutedEventArgs args)
        {
            tb.Unloaded -= OnUnloadedCleanup;
            CompositionTarget.Rendering -= Tick;
        }
    }
}
