using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;

namespace Imgagent.App.Controls;

/// <summary>
/// 区域标注画布：圈画/涂色/方框/圆圈/橡皮，标出"这里需要改"或"这里保留"。
/// 所有坐标归一化（0~1），导出到任意分辨率都对齐。
/// 马克笔导出用蒙版式非叠加（先 mask 再统一 alpha 贴回），重复涂色不加深。
/// </summary>
public sealed class RegionCanvas : Control
{
    public enum RegionIntent { Modify, Keep }
    public enum RegionTool { Marker, Brush, Rectangle, Ellipse, Eraser }

    private abstract class Shape
    {
        public required Color Color { get; init; }
        public required RegionTool Tool { get; init; }
        public abstract void DrawPreview(DrawingContext ctx, double w, double h, double minSide);
        public abstract void DrawExport(SKCanvas canvas, int w, int h);
    }

    private sealed class FreeStroke : Shape
    {
        public required double Size { get; init; }
        public List<(double X, double Y)> Points { get; } = new();

        public override void DrawPreview(DrawingContext ctx, double w, double h, double minSide)
        {
            var radiusPx = Math.Max(2.0, Size * minSide / 2.0);
            var brush = new SolidColorBrush(Color);
            if (Points.Count == 1)
            {
                var (x, y) = Points[0];
                ctx.DrawEllipse(brush, null, new Point(x * w, y * h), radiusPx, radiusPx);
                return;
            }
            for (int i = 1; i < Points.Count; i++)
            {
                var (x0, y0) = Points[i - 1];
                var (x1, y1) = Points[i];
                ctx.DrawLine(new Pen(brush, radiusPx * 2, lineCap: PenLineCap.Round,
                                     lineJoin: PenLineJoin.Round),
                             new Point(x0 * w, y0 * h), new Point(x1 * w, y1 * h));
            }
        }

        public override void DrawExport(SKCanvas canvas, int w, int h)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(Color.R, Color.G, Color.B, 255),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            var sw = (float)Math.Max(3.0, Size * Math.Min(w, h));
            if (Points.Count == 1)
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle((float)(Points[0].X * w), (float)(Points[0].Y * h), sw / 2, paint);
                return;
            }
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = sw;
            using var path = new SKPath();
            path.MoveTo((float)(Points[0].X * w), (float)(Points[0].Y * h));
            for (int i = 1; i < Points.Count; i++)
                path.LineTo((float)(Points[i].X * w), (float)(Points[i].Y * h));
            canvas.DrawPath(path, paint);
        }
    }

    private sealed class OutlineShape : Shape
    {
        public required bool IsEllipse { get; init; }
        public required double X0 { get; init; }
        public required double Y0 { get; init; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public required double StrokeWidth { get; init; }

        public override void DrawPreview(DrawingContext ctx, double w, double h, double minSide)
        {
            var radiusPx = Math.Max(2.0, StrokeWidth * minSide);
            var pen = new Pen(new SolidColorBrush(Color), radiusPx);
            var rect = new Rect(
                Math.Min(X0, X1) * w, Math.Min(Y0, Y1) * h,
                Math.Abs(X1 - X0) * w, Math.Abs(Y1 - Y0) * h);
            if (IsEllipse)
                ctx.DrawEllipse(null, pen, rect.Center, rect.Width / 2, rect.Height / 2);
            else
                ctx.DrawRectangle(null, pen, rect);
        }

        public override void DrawExport(SKCanvas canvas, int w, int h)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                Color = new SKColor(Color.R, Color.G, Color.B),
                StrokeWidth = (float)Math.Max(3.0, StrokeWidth * Math.Min(w, h)),
            };
            var rect = new SKRect((float)(Math.Min(X0, X1) * w), (float)(Math.Min(Y0, Y1) * h),
                                  (float)(Math.Max(X0, X1) * w), (float)(Math.Max(Y0, Y1) * h));
            if (IsEllipse) canvas.DrawOval(rect, paint);
            else canvas.DrawRect(rect, paint);
        }
    }

    private readonly List<Shape> _shapes = new();
    private Shape? _current;
    private Point? _lastPointer;

    public static readonly StyledProperty<string> BrushColorProperty =
        AvaloniaProperty.Register<RegionCanvas, string>(nameof(BrushColor), "#FF3B30");
    public string BrushColor { get => GetValue(BrushColorProperty); set => SetValue(BrushColorProperty, value); }

    public static readonly StyledProperty<double> BrushSizeProperty =
        AvaloniaProperty.Register<RegionCanvas, double>(nameof(BrushSize), 24d);
    public double BrushSize { get => GetValue(BrushSizeProperty); set => SetValue(BrushSizeProperty, value); }

    public static readonly StyledProperty<RegionTool> ToolProperty =
        AvaloniaProperty.Register<RegionCanvas, RegionTool>(nameof(Tool), RegionTool.Marker);
    public RegionTool Tool { get => GetValue(ToolProperty); set => SetValue(ToolProperty, value); }

    public static readonly StyledProperty<RegionIntent> IntentProperty =
        AvaloniaProperty.Register<RegionCanvas, RegionIntent>(nameof(Intent), RegionIntent.Modify);
    public RegionIntent Intent { get => GetValue(IntentProperty); set => SetValue(IntentProperty, value); }

    public static readonly StyledProperty<string?> BaseImagePathProperty =
        AvaloniaProperty.Register<RegionCanvas, string?>(nameof(BaseImagePath));
    public string? BaseImagePath { get => GetValue(BaseImagePathProperty); set => SetValue(BaseImagePathProperty, value); }

    public event EventHandler<int>? RegionCountChanged;
    public int RegionCount => _shapes.Count;

    public RegionCanvas() { Focusable = true; ClipToBounds = true; }

    private Color EffectiveColor =>
        Color.TryParse(BrushColor, out var c) ? c
        : (Intent == RegionIntent.Keep ? Colors.Green : Colors.Red);

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var minSide = Math.Min(w, h);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(24, 52, 145, 250)),
                          new Rect(0, 0, w, h));
        foreach (var sh in _shapes) sh.DrawPreview(ctx, w, h, minSide);
        if (_current is not null) _current.DrawPreview(ctx, w, h, minSide);
        if (Tool == RegionTool.Eraser && _lastPointer is { } lp)
        {
            var rr = EraserRadius * minSide;
            ctx.DrawEllipse(null,
                new Pen(new SolidColorBrush(Colors.White, 0.9), 1.5, dashStyle: DashStyle.Dash),
                lp, rr, rr);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _lastPointer = p;

        if (Tool == RegionTool.Eraser)
        {
            EraseAt(p.X / Bounds.Width, p.Y / Bounds.Height);
            e.Pointer.Capture(this); e.Handled = true; return;
        }

        var fallback = Intent == RegionIntent.Keep ? Colors.Green : Colors.Red;
        var color = Color.TryParse(BrushColor, out var c) ? c : fallback;
        var normSize = Math.Clamp(
            (Tool == RegionTool.Marker ? BrushSize * 1.8 : BrushSize) /
            Math.Max(1, Math.Min(Bounds.Width, Bounds.Height)), 0.004, 0.5);
        var outlineW = Math.Clamp(BrushSize / Math.Max(1, Math.Min(Bounds.Width, Bounds.Height)), 0.004, 0.1);

        _current = Tool switch
        {
            RegionTool.Rectangle => new OutlineShape { Color = color, Tool = Tool, IsEllipse = false,
                X0 = p.X / Bounds.Width, Y0 = p.Y / Bounds.Height,
                X1 = p.X / Bounds.Width, Y1 = p.Y / Bounds.Height, StrokeWidth = outlineW },
            RegionTool.Ellipse => new OutlineShape { Color = color, Tool = Tool, IsEllipse = true,
                X0 = p.X / Bounds.Width, Y0 = p.Y / Bounds.Height,
                X1 = p.X / Bounds.Width, Y1 = p.Y / Bounds.Height, StrokeWidth = outlineW },
            _ => new FreeStroke { Color = color, Tool = Tool, Size = normSize },
        };
        if (_current is FreeStroke fst) fst.Points.Add((p.X / Bounds.Width, p.Y / Bounds.Height));
        e.Pointer.Capture(this); InvalidateVisual(); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _lastPointer = e.GetPosition(this);
        if (!IsVisible) { return; }
        if (Tool == RegionTool.Eraser)
        {
            if (_lastPointer is { } lp && Bounds.Width > 0 && Bounds.Height > 0)
                EraseAt(lp.X / Bounds.Width, lp.Y / Bounds.Height);
            InvalidateVisual(); return;
        }
        if (_current is null) { InvalidateVisual(); return; }
        var p = e.GetPosition(this);
        var nx = p.X / Bounds.Width; var ny = p.Y / Bounds.Height;

        if (_current is FreeStroke fst)
        {
            if (fst.Points.Count > 0)
            {
                var (lx, ly) = fst.Points[^1];
                var dx = nx - lx; var dy = ny - ly;
                if (dx * dx + dy * dy < 0.00000016) return;
            }
            fst.Points.Add((nx, ny));
        }
        else if (_current is OutlineShape os) { os.X1 = nx; os.Y1 = ny; }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Tool == RegionTool.Eraser) { e.Pointer.Capture(null); e.Handled = true; return; }
        if (_current is not null)
        {
            if (_current is OutlineShape os &&
                Math.Abs(os.X1 - os.X0) < 0.005 && Math.Abs(os.Y1 - os.Y0) < 0.005)
                _current = null;
            else { _shapes.Add(_current); _current = null; }
        }
        e.Pointer.Capture(null); InvalidateVisual();
        RegionCountChanged?.Invoke(this, _shapes.Count);
        e.Handled = true;
    }

    private void EraseAt(double nx, double ny)
    {
        var r = EraserRadius;
        int removed = _shapes.RemoveAll(sh => HitShape(sh, nx, ny, r));
        if (removed > 0)
        {
            InvalidateVisual();
            RegionCountChanged?.Invoke(this, _shapes.Count);
        }
    }

    private double EraserRadius => Math.Clamp(
        (BrushSize * 1.5) / Math.Max(1, Math.Min(Bounds.Width, Bounds.Height)), 0.01, 0.5);

    private static bool HitShape(Shape sh, double nx, double ny, double r)
    {
        if (sh is FreeStroke fs)
        {
            foreach (var (x, y) in fs.Points)
            {
                var dx = x - nx; var dy = y - ny;
                if (dx * dx + dy * dy <= r * r) return true;
            }
            return false;
        }
        if (sh is OutlineShape os)
        {
            return nx >= Math.Min(os.X0, os.X1) - r && nx <= Math.Max(os.X0, os.X1) + r &&
                   ny >= Math.Min(os.Y0, os.Y1) - r && ny <= Math.Max(os.Y0, os.Y1) + r;
        }
        return false;
    }

    public void ClearRegions()
    {
        _shapes.Clear(); _current = null;
        InvalidateVisual();
        RegionCountChanged?.Invoke(this, 0);
    }

    public byte[]? ExportComposite()
    {
        if (_shapes.Count == 0) return null;
        var path = BaseImagePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var baseBmp = SKBitmap.Decode(path);
        if (baseBmp is null) return null;
        int w = baseBmp.Width, h = baseBmp.Height;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(baseBmp, 0, 0);

        // 蒙版式非叠加：自由笔迹以不透明画到 mask，mask 像素统一 alpha 后贴回
        using var mask = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var maskSurface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            var mc = maskSurface.Canvas;
            mc.Clear(SKColors.Transparent);
            foreach (var sh in _shapes.Where(x => x is FreeStroke)) sh.DrawExport(mc, w, h);
        }
        const byte unifiedAlpha = 110;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var px = mask.GetPixel(x, y);
                if (px.Alpha == 0) continue;
                mask.SetPixel(x, y, new SKColor(px.Red, px.Green, px.Blue, unifiedAlpha));
            }
        canvas.DrawBitmap(mask, 0, 0);

        foreach (var sh in _shapes.Where(x => x is OutlineShape)) sh.DrawExport(canvas, w, h);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 95);
        return png.ToArray();
    }
}