using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace QuickReply.Desktop.Services;

public static class WindowTransitionService
{
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IEasingFunction FadeInEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction FadeOutEase = new CubicEase { EasingMode = EasingMode.EaseIn };
    private static readonly ConditionalWeakTable<Window, WindowTransitionState> States = [];

    public static void Attach(Window window, bool animateOnLoaded = true, bool animateOnClosing = true)
    {
        var state = States.GetOrCreateValue(window);
        if (state.IsAttached)
        {
            return;
        }

        state.IsAttached = true;
        state.AnimateOnClosing = animateOnClosing;

        if (animateOnLoaded)
        {
            window.Opacity = 0d;
            window.Loaded += Window_OnLoaded;
        }

        if (animateOnClosing)
        {
            window.Closing += Window_OnClosing;
        }

        window.Closed += Window_OnClosed;
    }

    public static void ShowWithFade(Window window)
    {
        var state = States.GetOrCreateValue(window);
        var version = ++state.TransitionVersion;

        CancelOpacityAnimation(window);

        if (!window.IsVisible)
        {
            window.Opacity = 0d;
            window.Show();
        }

        BeginOpacityAnimation(window, version, 1d, FadeInDuration, FadeInEase, completedAction: null);
    }

    public static void HideWithFade(Window window)
    {
        if (!window.IsVisible)
        {
            return;
        }

        var state = States.GetOrCreateValue(window);
        var version = ++state.TransitionVersion;
        CancelOpacityAnimation(window);

        BeginOpacityAnimation(
            window,
            version,
            0d,
            FadeOutDuration,
            FadeOutEase,
            () =>
            {
                if (state.TransitionVersion != version)
                {
                    return;
                }

                window.Hide();
                window.Opacity = 0d;
            });
    }

    public static void SuppressNextCloseAnimation(Window window)
    {
        var state = States.GetOrCreateValue(window);
        state.SuppressNextCloseAnimation = true;
        CancelOpacityAnimation(window);
    }

    private static void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        var state = States.GetOrCreateValue(window);
        var version = ++state.TransitionVersion;
        CancelOpacityAnimation(window);
        window.Opacity = 0d;
        BeginOpacityAnimation(window, version, 1d, FadeInDuration, FadeInEase, completedAction: null);
    }

    private static void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        var state = States.GetOrCreateValue(window);
        if (!state.AnimateOnClosing)
        {
            return;
        }

        if (state.SuppressNextCloseAnimation)
        {
            state.SuppressNextCloseAnimation = false;
            return;
        }

        if (!window.IsVisible || window.Opacity <= 0.01d)
        {
            return;
        }

        e.Cancel = true;
        var version = ++state.TransitionVersion;
        CancelOpacityAnimation(window);

        BeginOpacityAnimation(
            window,
            version,
            0d,
            FadeOutDuration,
            FadeOutEase,
            () =>
            {
                if (state.TransitionVersion != version)
                {
                    return;
                }

                state.SuppressNextCloseAnimation = true;
                window.Close();
            });
    }

    private static void Window_OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        var state = States.GetOrCreateValue(window);
        state.TransitionVersion++;
    }

    private static void BeginOpacityAnimation(
        Window window,
        int version,
        double targetOpacity,
        Duration duration,
        IEasingFunction easingFunction,
        Action? completedAction)
    {
        var animation = new DoubleAnimation
        {
            From = double.IsNaN(window.Opacity) ? 1d : Math.Clamp(window.Opacity, 0d, 1d),
            To = targetOpacity,
            Duration = duration,
            EasingFunction = easingFunction,
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            var state = States.GetOrCreateValue(window);
            if (state.TransitionVersion != version)
            {
                return;
            }

            window.Opacity = targetOpacity;
            completedAction?.Invoke();
        };

        window.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void CancelOpacityAnimation(Window window)
    {
        window.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private sealed class WindowTransitionState
    {
        public bool IsAttached { get; set; }

        public bool AnimateOnClosing { get; set; }

        public bool SuppressNextCloseAnimation { get; set; }

        public int TransitionVersion { get; set; }
    }
}
