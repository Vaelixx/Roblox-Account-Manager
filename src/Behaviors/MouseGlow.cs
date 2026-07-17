using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace RobloxAccountManager.Behaviors;

/// <summary>
/// Attached behaviour that paints a soft radial "spotlight" glow which follows the
/// mouse while it hovers the element. Rendered in an <see cref="Adorner"/> layered on
/// top of the target, clipped to the element's rounded-rect bounds, so it never
/// affects layout or hit-testing. Enable with <c>beh:MouseGlow.Enable="True"</c>.
/// </summary>
public static class MouseGlow
{
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached("Enable", typeof(bool), typeof(MouseGlow),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject d) => (bool)d.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject d, bool v) => d.SetValue(EnableProperty, v);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.MouseEnter += OnEnter;
            fe.MouseLeave += OnLeave;
            fe.MouseMove  += OnMove;
        }
        else
        {
            fe.MouseEnter -= OnEnter;
            fe.MouseLeave -= OnLeave;
            fe.MouseMove  -= OnMove;
        }
    }

    private static readonly DependencyProperty AdornerProperty =
        DependencyProperty.RegisterAttached("_glowAdorner", typeof(GlowAdorner), typeof(MouseGlow));

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        var layer = AdornerLayer.GetAdornerLayer(fe);
        if (layer == null) return;
        var adorner = (GlowAdorner?)fe.GetValue(AdornerProperty);
        if (adorner == null)
        {
            adorner = new GlowAdorner(fe);
            fe.SetValue(AdornerProperty, adorner);
            layer.Add(adorner);
        }
        adorner.SetPoint(e.GetPosition(fe));
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (fe.GetValue(AdornerProperty) is GlowAdorner a) a.SetPoint(e.GetPosition(fe));
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (fe.GetValue(AdornerProperty) is GlowAdorner a)
        {
            AdornerLayer.GetAdornerLayer(fe)?.Remove(a);
            fe.SetValue(AdornerProperty, null);
        }
    }

    private sealed class GlowAdorner : Adorner
    {
        private Point _p;
        private const double Radius = 170;

        public GlowAdorner(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
        }

        public void SetPoint(Point p) { _p = p; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            var fe = (FrameworkElement)AdornedElement;
            double w = fe.ActualWidth, h = fe.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Accent-tinted glow; fall back to white if the resource is missing.
            Color accent = Colors.White;
            if (Application.Current?.TryFindResource("AccentBrush") is SolidColorBrush sb) accent = sb.Color;

            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                GradientOrigin = _p,
                Center = _p,
                RadiusX = Radius,
                RadiusY = Radius,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(46, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb(14, accent.R, accent.G, accent.B), 0.55),
                    new GradientStop(Color.FromArgb(0,  accent.R, accent.G, accent.B), 1),
                }
            };
            brush.Freeze();

            double corner = (fe as Border)?.CornerRadius.TopLeft ?? 10;
            var rect = new Rect(0, 0, w, h);
            var clip = new RectangleGeometry(rect, corner, corner);
            dc.PushClip(clip);
            dc.DrawRectangle(brush, null, rect);
            dc.Pop();
        }
    }
}
