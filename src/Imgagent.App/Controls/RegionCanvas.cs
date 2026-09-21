using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;

namespace Imgagent.App.Controls;

/// <summary>
/// 区域标注画布：在底图上圈画 / 涂色 / 框选，标出"这里需要改"。
///
/// 关键设计：
///   · 坐标**归一化**（0~1）—— 与控件尺寸、缩放级别无关，
///     标记天然"绑定"在图片上，缩放/平移时跟随。
///   · **蒙版式薄层**：所有标记先画到不透明 mask，再整体以固定 alpha 贴回 →
///     重复涂抹**颜色不加深**，始终是薄薄一层。
///   · 支持**缩放/平移**：滚轮缩放、右键/中键/空格+左键平移、`0` 复位。
///   · 无"语义"概念：统一用当前画笔色表示"这里要改"。
/// </summary>
public sealed class RegionCanvas : Control
{
    /// <summary>绘制工具。</summary>
    public enum RegionTool
    {
        /// <summary>马克笔（蒙版薄层，半透明）。</summary>
        Marker,
        /// <summary>细画笔（可调粗细）。</summary>
        Brush,
        /// <summary>矩形框。</summary>
        Rectangle,
        /// <summary>椭圆框。</summary>
        Ellipse,
        /// <summary>橡皮。</summary>
        Eraser,
    }

    private abstract class Shape
    {
        public required Color Color { get; init; }
        public required double Size { get; init; }   // 归一化线宽
        public abstract void DrawPreview(DrawingContext ctx, double w, double h, double minSide);
        public abstract void RenderToMask(SKCanvas canvas, int w, int h);
        public abstract bool HitTest(double nx, double ny, double r);
    }

    /// <summary>自由笔迹（马克笔 / 画笔）。</summary>
    private sealed class FreeStroke : Shape
    {
        public List<(double X, double Y)> Points { get; } = new();

        public override void DrawPreview(DrawingContext ctx, double w, double h, double minSide)
        {
            var radiusPx = Math.Max(1.5, Size * minSide / 2.0);
            // 预览固定半透明（真正结果以导出为准）
            var brush = new SolidColorBrush(Color.FromArgb(110, Color.R, Color.G, Color.B));
            if (Points.Count == 1)
            {
                var (x, y) = Points[0];
                ctx.DrawEllipse(brush, null, new Point(x * w, y * h), radiusPx, radiusPx);
                return;
            }
            var pen = new Pen(brush, radiusPx * 2,
                              lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            for (int i = 1; i < Points.Count; i++)
            {
                var (x0, y0) = Points[i - 1];
                var (x1, y1) = Points[i];
                ctx.DrawLine(pen, new Point(x0 * w, y0 * h), new Point(x1 * w, y1 * h));
            }
        }

        public override void RenderToMask(SKCanvas canvas, int w, int h)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(Color.R, Color.G, Color.B, 255),  // mask 内不透明
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            var sw = (float)Math.Max(2.0, Size * Math.Min(w, h));
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

        public override bool HitTest(double nx, double ny, double r)
        {
            foreach (var (x, y) in Points)
            {
                var dx = x - nx; var dy = y - ny;
                if (dx * dx + dy * dy <= r * r) return true;
            }
            return false;
        }
    }

    /// <summary>矩形 / 椭圆框。</summary>
    private sealed class OutlineShape : Shape
    {
        public required bool IsEllipse { get; init; }
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }

        public override void DrawPreview(DrawingContext ctx, double w, double h, double minSide)
        {
            var radiusPx = Math.Max(1.5, Size * minSide);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, Color.R, Color.G, Color.B)), radiusPx);
            var rect = new Rect(
                Math.Min(X0, X1) * w, Math.Min(Y0, Y1) * h,
                Math.Abs(X1 - X0) * w, Math.Abs(Y1 - Y0) * h);
            if (IsEllipse) ctx.DrawEllipse(null, pen, rect.Center, rect.Width / 2, rect.Height / 2);
            else ctx.DrawRectangle(null, pen, rect);
        }

        public override void RenderToMask(SKCanvas canvas, int w, int h)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                Color = new SKColor(Color.R, Color.G, Color.B, 255),
                StrokeWidth = (float)Math.Max(2.0, Size * Math.Min(w, h)),
            };
            var rect = new SKRect((float)(Math.Min(X0, X1) * w), (float)(Math.Min(Y0, Y1) * h),
                                  (float)(Math.Max(X0, X1) * w), (float)(Math.Max(Y0, Y1) * h));
            if (IsEllipse) canvas.DrawOval(rect, paint);
            else canvas.DrawRect(rect, paint);
        }

        public override bool HitTest(double nx, double ny, double r)
            => nx >= Math.Min(X0, X1) - r && nx <= Math.Max(X0, X1) + r &&
               ny >= Math.Min(Y0, Y1) - r && ny <= Math.Max(Y0, Y1) + r;
    }

    private readonly List<Shape> _shapes = new();
    private Shape? _current;

    // ---------------------------------------------------------------- 可绑定属性
    public static readonly StyledProperty<string> BrushColorProperty =
        AvaloniaProperty.Register<RegionCanvas, string>(nameof(BrushColor), "#FF3B30");
    public string BrushColor { get => GetValue(BrushColorProperty); set => SetValue(BrushColorProperty, value); }

    /// <summary>笔刷粗细（像素；≤0 = 自动按最短边的 3%）。</summary>
    public static readonly StyledProperty<double> BrushSizeProperty =
        AvaloniaProperty.Register<RegionCanvas, double>(nameof(BrushSize), 24d);
    public double BrushSize { get => GetValue(BrushSizeProperty); set => SetValue(BrushSizeProperty, value); }

    public static readonly StyledProperty<RegionTool> ToolProperty =
        AvaloniaProperty.Register<RegionCanvas, RegionTool>(nameof(Tool), RegionTool.Marker);
    public RegionTool Tool { get => GetValue(ToolProperty); set => SetValue(ToolProperty, value); }

    public static readonly StyledProperty<string?> BaseImagePathProperty =
        AvaloniaProperty.Register<RegionCanvas, string?>(nameof(BaseImagePath));
    public string? BaseImagePath { get => GetValue(BaseImagePathProperty); set => SetValue(BaseImagePathProperty, value); }

    /// <summary>薄层不透明度（0-255）。固定值 → 重复涂色不加深。</summary>
    public static readonly StyledProperty<int> LayerAlphaProperty =
        AvaloniaProperty.Register<RegionCanvas, int>(nameof(LayerAlpha), 110);
    public int LayerAlpha { get => GetValue(LayerAlphaProperty); set => SetValue(LayerAlphaProperty, value); }

    public event EventHandler<int>? RegionCountChanged;
    public int RegionCount => _shapes.Count;

    // ---------------------------------------------------------------- 视图变换（缩放/平移）
    private double _zoom = 1.0;
    private double _panX, _panY;
    private bool _panning;
    private Point _panStart;
    private bool _spaceDown;
    private Point? _lastPointer;

    public RegionCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void ResetView() { _zoom = 1.0; _panX = 0; _panY = 0; InvalidateVisual(); }

    public void ZoomBy(double factor)
    {
        var nz = Math.Clamp(_zoom * factor, 0.2, 8.0);
        if (Math.Abs(nz - _zoom) < 1e-6) return;
        _zoom = nz;
        InvalidateVisual();
    }

    private Color EffectiveColor => Color.TryParse(BrushColor, out var c) ? c : Colors.Red;

    /// <summary>控件坐标 → 图片归一化坐标（逆变换，考虑缩放/平移）。</summary>
    private (double Nx, double Ny) ToNormalized(Point p)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0 || h <= 0) return (0, 0);
        var cx = (p.X - w / 2 - _panX) / _zoom + w / 2;
        var cy = (p.Y - h / 2 - _panY) / _zoom + h / 2;
        return (cx / w, cy / h);
    }

    /// <summary>归一化线宽（与缩放无关 → 缩放后视觉粗细一致）。</summary>
    private double NormSize()
    {
        var minSide = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height));
        var basePx = BrushSize <= 0 ? minSide * 0.03 : BrushSize;
        return Math.Clamp(basePx / minSide, 0.002, 0.5);
    }

    private double EraserRadiusPx => Math.Max(6, BrushSize <= 0 ? 20 : BrushSize * 1.2);

    // ---------------------------------------------------------------- 渲染
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 缩放/平移变换 —— 标记与图片一起变换（绑定在图片上）
        var m = Matrix.CreateTranslation(-w / 2 - _panX, -h / 2 - _panY)
              * Matrix.CreateScale(_zoom, _zoom)
              * Matrix.CreateTranslation(w / 2, h / 2);
        using (ctx.PushTransform(m))
        {
            var minSide = Math.Min(w, h);
            foreach (var sh in _shapes) sh.DrawPreview(ctx, w, h, minSide);
            if (_current is not null) _current.DrawPreview(ctx, w, h, minSide);
        }

        if (Tool == RegionTool.Eraser && _lastPointer is { } lp)
        {
            var rr = EraserRadiusPx;
            ctx.DrawEllipse(null,
                new Pen(new SolidColorBrush(Colors.White, 0.9), 1.5, dashStyle: DashStyle.Dash),
                lp, rr, rr);
        }
    }

    // ---------------------------------------------------------------- 交互
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        _lastPointer = p;
        var props = e.GetCurrentPoint(this).Properties;

        // 平移：右键 / 中键 / 空格+左键
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed ||
            (props.IsLeftButtonPressed && _spaceDown))
        {
            _panning = true; _panStart = p;
            e.Pointer.Capture(this); e.Handled = true; return;
        }

        if (!props.IsLeftButtonPressed) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var (nx, ny) = ToNormalized(p);

        if (Tool == RegionTool.Eraser)
        {
            EraseAt(nx, ny);
            e.Pointer.Capture(this); e.Handled = true; return;
        }

        var color = EffectiveColor;
        var size = NormSize();
        _current = Tool switch
        {
            RegionTool.Rectangle => new OutlineShape
            { Color = color, Size = size, IsEllipse = false, X0 = nx, Y0 = ny, X1 = nx, Y1 = ny },
            RegionTool.Ellipse => new OutlineShape
            { Color = color, Size = size, IsEllipse = true, X0 = nx, Y0 = ny, X1 = nx, Y1 = ny },
            _ => new FreeStroke { Color = color, Size = size },
        };
        if (_current is FreeStroke fs) fs.Points.Add((nx, ny));

        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _lastPointer = p;

        if (_panning)
        {
            _panX += p.X - _panStart.X;
            _panY += p.Y - _panStart.Y;
            _panStart = p;
            InvalidateVisual();
            return;
        }

        if (Tool == RegionTool.Eraser)
        {
            if (_current is null && Bounds.Width > 0)
            {
                var (ex, ey) = ToNormalized(p);
                EraseAt(ex, ey);
            }
            InvalidateVisual();
            return;
        }

        if (_current is null) { InvalidateVisual(); return; }
        var (nx, ny) = ToNormalized(p);

        if (_current is FreeStroke fs2)
        {
            if (fs2.Points.Count > 0)
            {
                var (lx, ly) = fs2.Points[^1];
                var dx = nx - lx; var dy = ny - ly;
                if (dx * dx + dy * dy < 1e-7) return;   // 降采样
            }
            fs2.Points.Add((nx, ny));
        }
        else if (_current is OutlineShape os) { os.X1 = nx; os.Y1 = ny; }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning) { _panning = false; e.Pointer.Capture(null); e.Handled = true; return; }
        if (Tool == RegionTool.Eraser) { e.Pointer.Capture(null); e.Handled = true; return; }

        if (_current is not null)
        {
            if (_current is OutlineShape os &&
                Math.Abs(os.X1 - os.X0) < 0.005 && Math.Abs(os.Y1 - os.Y0) < 0.005)
                _current = null;
            else { _shapes.Add(_current); _current = null; }
        }
        e.Pointer.Capture(null);
        InvalidateVisual();
        RegionCountChanged?.Invoke(this, _shapes.Count);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space) { _spaceDown = true; e.Handled = true; }
        if (e.Key is Key.D0 or Key.NumPad0) { ResetView(); e.Handled = true; }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) { _spaceDown = false; e.Handled = true; }
    }

    private void EraseAt(double nx, double ny)
    {
        var r = NormSize() * 2.5;
        if (_shapes.RemoveAll(sh => sh.HitTest(nx, ny, r)) > 0)
        {
            InvalidateVisual();
            RegionCountChanged?.Invoke(this, _shapes.Count);
        }
    }

    public void ClearRegions()
    {
        _shapes.Clear(); _current = null;
        InvalidateVisual();
        RegionCountChanged?.Invoke(this, 0);
    }

    // ---------------------------------------------------------------- 快照（布局切换同步）
    public sealed class RegionSnapshot
    {
        public List<StrokeData> Strokes { get; init; } = new();
    }

    public sealed class StrokeData
    {
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double Size { get; set; }
        public bool IsOutline { get; set; }
        public bool IsEllipse { get; set; }
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public List<double> Points { get; set; } = new();
    }

    public RegionSnapshot ExportShapes()
    {
        var snap = new RegionSnapshot();
        foreach (var sh in _shapes)
        {
            if (sh is FreeStroke fs)
            {
                var d = new StrokeData { R = sh.Color.R, G = sh.Color.G, B = sh.Color.B, Size = sh.Size };
                foreach (var (x, y) in fs.Points) { d.Points.Add(x); d.Points.Add(y); }
                snap.Strokes.Add(d);
            }
            else if (sh is OutlineShape os)
            {
                snap.Strokes.Add(new StrokeData
                {
                    R = sh.Color.R, G = sh.Color.G, B = sh.Color.B, Size = sh.Size,
                    IsOutline = true, IsEllipse = os.IsEllipse,
                    X0 = os.X0, Y0 = os.Y0, X1 = os.X1, Y1 = os.Y1,
                });
            }
        }
        return snap;
    }

    public void ImportShapes(RegionSnapshot? snap)
    {
        _shapes.Clear(); _current = null;
        if (snap is not null)
        {
            foreach (var d in snap.Strokes)
            {
                var color = Color.FromRgb((byte)d.R, (byte)d.G, (byte)d.B);
                if (d.IsOutline)
                {
                    _shapes.Add(new OutlineShape
                    {
                        Color = color, Size = d.Size, IsEllipse = d.IsEllipse,
                        X0 = d.X0, Y0 = d.Y0, X1 = d.X1, Y1 = d.Y1,
                    });
                }
                else
                {
                    var fs = new FreeStroke { Color = color, Size = d.Size };
                    for (int i = 0; i + 1 < d.Points.Count; i += 2)
                        fs.Points.Add((d.Points[i], d.Points[i + 1]));
                    _shapes.Add(fs);
                }
            }
        }
        InvalidateVisual();
        RegionCountChanged?.Invoke(this, _shapes.Count);
    }

    // ---------------------------------------------------------------- 导出
    /// <summary>
    /// 合成「底图 + 标注薄层」为 PNG。
    /// **蒙版式非叠加**：标记先画到不透明 mask，再统一替换为「原色 + LayerAlpha」
    /// 贴回 → 重复涂抹不加深，始终薄薄一层。
    /// </summary>
    public byte[]? ExportComposite()
    {
        if (_shapes.Count == 0) return null;
        var path = BaseImagePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var baseBmp = SKBitmap.Decode(path);
        if (baseBmp is null) return null;

        int w = baseBmp.Width, h = baseBmp.Height;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.DrawBitmap(baseBmp, 0, 0);

        using var mask = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var ms = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            ms.Canvas.Clear(SKColors.Transparent);
            foreach (var sh in _shapes) sh.RenderToMask(ms.Canvas, w, h);
            ms.Canvas.Flush();
            using var snap = ms.Snapshot();
            snap.ReadPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul),
                            mask.GetPixels(), w * 4, 0, 0);
        }

        byte alpha = (byte)Math.Clamp(LayerAlpha, 10, 255);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var px = mask.GetPixel(x, y);
                if (px.Alpha == 0) continue;
                mask.SetPixel(x, y, new SKColor(px.Red, px.Green, px.Blue, alpha));
            }
        surface.Canvas.DrawBitmap(mask, 0, 0);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 95);
        return png.ToArray();
    }

    /// <summary>
    /// 单独导出「修改区域蒙版」PNG：**白色=需要修改，黑色=保持不变**。
    /// 供支持 mask/inpainting 的模型使用（把"修改数据"显式传给模型）。
    /// </summary>
    public byte[]? ExportMask()
    {
        if (_shapes.Count == 0) return null;
        var path = BaseImagePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var baseBmp = SKBitmap.Decode(path);
        if (baseBmp is null) return null;

        int w = baseBmp.Width, h = baseBmp.Height;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        foreach (var sh in _shapes) sh.RenderToMask(surface.Canvas, w, h);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }
}
