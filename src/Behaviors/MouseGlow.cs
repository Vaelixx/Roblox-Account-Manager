using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace RobloxAccountManager.Behaviors;

/// <summary>
/// Attached behaviour that paints an icy "liquid glass" glow along the *edges* of the
/// element. The glow always emanates from the container border — never from the pointer
/// itself: the closer the mouse gets to a border, the brighter the rim lights up around
/// the point of the border nearest to the mouse. Rendered in an <see cref="Adorner"/>
/// layered on top of the target, clipped to the element's rounded-rect bounds, so it
/// never affects layout or hit-testing. Enable with <c>beh:MouseGlow.Enable="True"</c>.
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

        /// <summary>Pointer distance to a border at which the rim starts to light up.</summary>
        private const double Reach = 100;

        /// <summary>How far the frosted light is allowed to bleed inward from the border.</summary>
        private const double BandDepth = 16;

        /// <summary>Radius of the light pool that hugs the rim.</summary>
        private const double PoolRadius = 110;

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

            // Proximity drives intensity: touching the border = full glow, fading to
            // nothing towards the middle. Small elements scale the reach down so their
            // centre always rests dark.
            double reach = Math.Min(Reach, Math.Min(w, h) / 2.0);
            if (reach < 4) return;
            double edgeDist = Math.Min(Math.Min(_p.X, w - _p.X), Math.Min(_p.Y, h - _p.Y));
            double t = 1 - Math.Clamp(edgeDist / reach, 0, 1);
            t = t * t * (3 - 2 * t); // smoothstep — glassy, non-linear ramp
            if (t < 0.02) return;

            // The glow is anchored to the border itself: project the pointer onto the
            // nearest point of the rim and let the light radiate from there.
            Point rim = NearestRimPoint(_p, w, h);

            // Icy tint: theme accent frosted towards a pale glacier blue, white-hot core.
            Color accent = Colors.White;
            if (Application.Current?.TryFindResource("AccentBrush") is SolidColorBrush sb) accent = sb.Color;
            Color icy = Blend(accent, Color.FromRgb(0xBF, 0xE4, 0xFF), 0.62);

            double corner = (fe as Border)?.CornerRadius.TopLeft ?? 10;
            var rect  = new Rect(0, 0, w, h);
            var outer = new RectangleGeometry(rect, corner, corner);
            outer.Freeze();

            // Confine the frosted bleed to a band along the border, so light always
            // reads as coming *from* the edges — never as a blob under the cursor.
            double band = Math.Min(BandDepth, Math.Min(w, h) / 3.0);
            double innerCorner = Math.Max(0, corner - band * 0.5);
            var innerRect = new Rect(band, band, Math.Max(0, w - band * 2), Math.Max(0, h - band * 2));
            var inner = new RectangleGeometry(innerRect, innerCorner, innerCorner);
            var ringClip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            ringClip.Freeze();

            // Soft light pool hugging the rim (frosted bleed).
            var pool = new RadialGradientBrush
            {
                MappingMode    = BrushMappingMode.Absolute,
                GradientOrigin = rim,
                Center         = rim,
                RadiusX        = PoolRadius,
                RadiusY        = PoolRadius,
                GradientStops  =
                {
                    new GradientStop(Color.FromArgb((byte)(96 * t), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb((byte)(48 * t), icy.R, icy.G, icy.B), 0.30),
                    new GradientStop(Color.FromArgb((byte)(16 * t), icy.R, icy.G, icy.B), 0.62),
                    new GradientStop(Color.FromArgb(0,               icy.R, icy.G, icy.B), 1),
                }
            };
            pool.Freeze();

            dc.PushClip(ringClip);
            dc.DrawRectangle(pool, null, rect);
            dc.Pop();

            // Whisper of light along the whole rim, so the frame reads as one glass piece.
            var basePen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(28 * t), icy.R, icy.G, icy.B)), 1.0);
            basePen.Freeze();

            // Crisp specular highlight on the rim segment nearest the pointer — the
            // "liquid glass" edge. Lit by a brighter copy of the same pool.
            var rimBrush = new RadialGradientBrush
            {
                MappingMode    = BrushMappingMode.Absolute,
                GradientOrigin = rim,
                Center         = rim,
                RadiusX        = PoolRadius,
                RadiusY        = PoolRadius,
                GradientStops  =
                {
                    new GradientStop(Color.FromArgb((byte)(230 * t), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb((byte)(140 * t), icy.R, icy.G, icy.B), 0.35),
                    new GradientStop(Color.FromArgb(0,               icy.R, icy.G, icy.B), 1),
                }
            };
            rimBrush.Freeze();
            var rimPen = new Pen(rimBrush, 1.4);
            rimPen.Freeze();

            var strokeRect = new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1));
            dc.PushClip(outer);
            dc.DrawRoundedRectangle(null, basePen, strokeRect, corner, corner);
            dc.DrawRoundedRectangle(null, rimPen,  strokeRect, corner, corner);
            dc.Pop();
        }

        /// <summary>Closest point on the element's border to <paramref name="p"/>.</summary>
        private static Point NearestRimPoint(Point p, double w, double h)
        {
            double x = Math.Clamp(p.X, 0, w), y = Math.Clamp(p.Y, 0, h);
            double dl = x, dr = w - x, dt = y, db = h - y;
            double m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            if (m == dl) return new Point(0, y);
            if (m == dr) return new Point(w, y);
            if (m == dt) return new Point(x, 0);
            return new Point(x, h);
        }

        /// <summary>Linear blend of two colours; <paramref name="amount"/> = share of <paramref name="b"/>.</summary>
        private static Color Blend(Color a, Color b, double amount)
        {
            double ia = 1 - amount;
            return Color.FromRgb(
                (byte)(a.R * ia + b.R * amount),
                (byte)(a.G * ia + b.G * amount),
                (byte)(a.B * ia + b.B * amount));
        }
    }
}
