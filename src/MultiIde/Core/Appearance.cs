using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MultiIde.Core;

/// <summary>
/// UI motion: hover transitions, popup/panel animations and smooth scrolling.
/// Everything honors <see cref="Settings.AnimationMode"/> ("animated"/"instant")
/// and <see cref="Settings.AnimationSpeed"/>.
/// </summary>
public static class Anim
{
    private static Styles? _transitionStyles;

    private static Settings S => IdeServices.Settings;
    public static bool Enabled => S.AnimationMode != "instant";

    /// <summary>A base duration scaled by the speed setting (0 when animations are off).</summary>
    public static TimeSpan Duration(double baseMs) =>
        Enabled ? TimeSpan.FromMilliseconds(baseMs / Math.Clamp(S.AnimationSpeed, 0.1, 5)) : TimeSpan.Zero;

    /// <summary>(Re)installs hover/press transitions for buttons, list items and tabs.</summary>
    public static void ApplyGlobalStyles()
    {
        var app = Application.Current;
        if (app is null) return;
        if (_transitionStyles is not null) app.Styles.Remove(_transitionStyles);
        // Hover highlights are deliberately not animated: a fading-out highlight left several
        // buttons lit at once while moving the mouse. Only popups and panels animate.
        _transitionStyles = new Styles();
        app.Styles.Add(_transitionStyles);
    }

    private static bool _popupsHooked;

    /// <summary>
    /// Animates every popup when it opens: menus and sub-menus, context menus, flyouts,
    /// combo box drop-downs, tooltips and the completion list.
    /// </summary>
    public static void InstallPopupAnimations()
    {
        if (_popupsHooked) return;
        _popupsHooked = true;
        Avalonia.Controls.Primitives.Popup.IsOpenProperty.Changed.AddClassHandler<Avalonia.Controls.Primitives.Popup>((popup, e) =>
        {
            if (e.NewValue is not true || popup.Child is not Control child) return;
            var isTip = popup.TemplatedParent is ToolTip || popup.Parent is ToolTip;
            Appear(child, isTip ? 90 : 130, isTip ? 3 : -6, 0.97);
        });
    }

    /// <summary>Fades and slides a control in once it is attached.</summary>
    public static void Appear(Control c, double baseMs = 160, double offsetY = 8, double fromScale = 0.98)
    {
        if (!Enabled) return;
        var d = Duration(baseMs);
        c.Opacity = 0;
        c.RenderTransform = TransformOperations.Parse($"translateY({offsetY}px) scale({fromScale})");
        c.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = d, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = d, Easing = new CubicEaseOut() },
        };
        void Start()
        {
            c.Opacity = 1;
            c.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
        }
        if (c.IsAttachedToVisualTree()) Avalonia.Threading.Dispatcher.UIThread.Post(Start, Avalonia.Threading.DispatcherPriority.Render);
        else c.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Start, Avalonia.Threading.DispatcherPriority.Render);
    }

    /// <summary>Animates a value on the render loop (ease-out cubic). Instant mode jumps to the end.</summary>
    public static void Tween(Visual host, double from, double to, double baseMs, Action<double> step, Action? done = null)
    {
        var d = Duration(baseMs).TotalMilliseconds;
        var top = TopLevel.GetTopLevel(host);
        if (d <= 0 || top is null || Math.Abs(to - from) < 0.5)
        {
            step(to);
            done?.Invoke();
            return;
        }
        TimeSpan? start = null;
        void Frame(TimeSpan now)
        {
            start ??= now;
            var t = Math.Clamp((now - start.Value).TotalMilliseconds / d, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            step(from + (to - from) * eased);
            if (t < 1) top.RequestAnimationFrame(Frame);
            else done?.Invoke();
        }
        top.RequestAnimationFrame(Frame);
    }

    // ---- Smooth scrolling -------------------------------------------------------

    private sealed class ScrollState
    {
        public double Target;
        public bool Running;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    /// <summary>
    /// Routes mouse-wheel scrolling of every ScrollViewer in the window through an eased animation.
    /// The innermost viewer that can scroll in the wheel direction wins, so nested lists behave.
    /// </summary>
    public static void InstallSmoothScrolling(TopLevel top)
    {
        top.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!S.SmoothScrolling || !Enabled || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            var vertical = Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var delta = vertical ? e.Delta.Y : (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y);
            if (delta == 0) return;

            for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
            {
                if (v is not ScrollViewer sv) continue;
                var max = vertical ? sv.Extent.Height - sv.Viewport.Height : sv.Extent.Width - sv.Viewport.Width;
                if (max <= 0) continue;
                var cur = vertical ? sv.Offset.Y : sv.Offset.X;
                var state = States.GetOrCreateValue(sv);
                if (!state.Running) state.Target = cur;
                var step = Math.Max(1, S.ScrollLines) * 18 * Math.Sign(delta) * Math.Max(1, Math.Abs(delta));
                var target = Math.Clamp(state.Target - step, 0, max);
                if (Math.Abs(target - cur) < 0.5 && !state.Running) continue; // at the edge: let a parent scroll
                state.Target = target;
                e.Handled = true;
                if (!state.Running) Run(top, sv, state, vertical);
                return;
            }
        }, RoutingStrategies.Tunnel);
    }

    private static void Run(TopLevel top, ScrollViewer sv, ScrollState state, bool vertical)
    {
        state.Running = true;
        var smoothing = Math.Clamp(0.22 * S.AnimationSpeed, 0.08, 0.9);
        void Frame(TimeSpan _)
        {
            var cur = vertical ? sv.Offset.Y : sv.Offset.X;
            var next = cur + (state.Target - cur) * smoothing;
            if (Math.Abs(state.Target - next) < 0.5) next = state.Target;
            sv.Offset = vertical ? new Vector(sv.Offset.X, next) : new Vector(next, sv.Offset.Y);
            if (next != state.Target) top.RequestAnimationFrame(Frame);
            else state.Running = false;
        }
        top.RequestAnimationFrame(Frame);
    }
}
