using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;

namespace ImgHub.App.Controls;

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
    /// <summary>
    /// 绘制工具。
    /// ⚠️ 顺序即 UI 下拉顺序 —— 必须与 <c>MainViewModel.ToolNames</c> **严格对齐**。
    ///
    /// v0.5.36：**去掉「画笔」**（用户要求）—— 它与「马克笔」走完全相同的绘制路径，
    ///   仅默认粗细不同，属重复功能。现在自由笔迹只保留「马克笔」。
    /// </summary>
    public enum RegionTool
    {
        /// <summary>矩形框。</summary>
        Rectangle,
        /// <summary>椭圆（圆圈）框。</summary>
        Ellipse,
        /// <summary>马克笔（蒙版薄层，半透明）。</summary>
        Marker,
        /// <summary>橡皮。</summary>
        Eraser,
    }

    /// <summary>
    /// 形状基类。
    ///
    /// ⚠️ v0.5 约定：<see cref="DrawPreview"/> 必须用**完全不透明**的颜色绘制，
    /// 由外层 <c>Render</c> 统一套一个 <c>PushOpacity(LayerAlpha)</c> 组。
    /// 这样"同一条内不叠加、跨条也不叠加"，与导出 mask 的语义**完全一致**。
    /// （旧实现让每段 DrawLine 自带 alpha=110 → 段与段重叠处 alpha 累加 →
    ///   按住拖动会越来越深直至不透明，正是用户报的问题。）
    /// </summary>
    private abstract class Shape
    {
        public required Color Color { get; init; }
        public required double Size { get; init; }   // 归一化线宽
        public abstract void DrawPreview(DrawingContext ctx, double w, double h, double minSide);
        public abstract void RenderToMask(SKCanvas canvas, int w, int h);

        /// <summary>
        /// 渲染到**蒙版用途**的画布：矩形/椭圆要**填充**（区域语义），
        /// 自由笔画保持笔宽描边。
        ///
        /// ⚠️ 为什么需要它（v5.27.0）：<see cref="RenderToMask"/> 是给
        /// "底图 + 标注薄层"的合成图用的（描边更美观，避免遮挡原图内容）；
        /// 但作为 **mask** 送给模型时，文档要的是"哪些像素要改"的**区域**，
        /// 只给一圈框线等于没指定区域 → 蒙版形同虚设。
        /// </summary>
        public virtual void RenderToMaskFilled(SKCanvas canvas, int w, int h)
            => RenderToMask(canvas, w, h);

        public abstract bool HitTest(double nx, double ny, double r);
    }

    /// <summary>自由笔迹（马克笔 / 画笔）。</summary>
    private sealed class FreeStroke : Shape
    {
        public List<(double X, double Y)> Points { get; } = new();

        public override void DrawPreview(DrawingContext ctx, double w, double h, double minSide)
        {
            var radiusPx = Math.Max(1.5, Size * minSide / 2.0);
            // ⚠️ 不透明色（alpha 由外层 PushOpacity 统一施加）→ 段间不叠加
            var brush = new SolidColorBrush(Color.FromRgb(Color.R, Color.G, Color.B));
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

        /// <summary>
        /// 蒙版用途：用**明显更宽**的笔触把涂抹路径渲染成"区域"。
        ///
        /// ⚠️ 为什么必须加粗（v5.27.0）：
        ///   作为"底图 + 标注薄层"的合成图时，细笔触（默认 3px）是**刻意**的
        ///   —— 不遮挡原图内容。但作为 mask 送给模型时，文档要的是"哪些像素要改"
        ///   的**区域**：3px 的线条对模型来说等于"没有指定区域"，蒙版会形同虚设。
        ///   同时鼠标快速拖动时采样点较稀疏，细线还会出现**断点**，
        ///   导致蒙版出现本该连成一片的区域被切成碎片。
        ///   所以这里按"最短边的固定比例"给足宽度，并用 Round cap/join 保证连续。
        /// </summary>
        public override void RenderToMaskFilled(SKCanvas canvas, int w, int h)
        {
            int minSide = Math.Min(w, h);
            // 宽度取「用户笔刷 × 3」与「最短边 1.2%」的较大者，并设 12px 下限
            float sw = (float)Math.Max(12.0, Math.Max(Size * minSide * MaskStrokeBoost,
                                                       minSide * MaskMinStrokeRatio));
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(Color.R, Color.G, Color.B, 255),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            if (Points.Count == 1)
            {
                // 单击一次也应形成一个可见的区域（而非 1px 的点）
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
            // ⚠️ 用户需求（②）：矩形/椭圆改为**实心填充**（此前只描边）。
            //    理由：蒙版语义是"这片区域要改"，实心才能让用户一眼看清覆盖范围；
            //    描边会让人以为只有那圈线生效。填充色 + 外层 PushOpacity 控制浓度。
            var brush = new SolidColorBrush(Color.FromRgb(Color.R, Color.G, Color.B));
            var rect = new Rect(
                Math.Min(X0, X1) * w, Math.Min(Y0, Y1) * h,
                Math.Abs(X1 - X0) * w, Math.Abs(Y1 - Y0) * h);

            if (IsEllipse) ctx.DrawEllipse(brush, null, rect.Center, rect.Width / 2, rect.Height / 2);
            else ctx.DrawRectangle(brush, null, rect);
        }

        /// <summary>
        /// 渲染到 mask 画布（合成图与蒙版共用）。
        ///
        /// ⚠️ 用户需求（②）：矩形/椭圆**填充整个区域**，与预览一致。
        ///   此前这里是描边（为了合成图"不遮挡原图"），但那样：
        ///     ① 预览与最终蒙版观感不符（用户以为只有框线生效）；
        ///     ② 作为 mask 送模型时只给一圈线 = 没指定区域（形同虚设）。
        ///   现在统一为填充 —— 浓度由外层 <c>LayerAlpha</c> / 颜色矩阵控制。
        /// </summary>
        public override void RenderToMask(SKCanvas canvas, int w, int h)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Fill,
                Color = new SKColor(Color.R, Color.G, Color.B, 255),
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

    // ---------------------------------------------------------------- 离屏扁平层缓存（v0.5.38 性能）
    /// <summary>
    /// 「所有标记压平到一张不透明位图」的**缓存**（需求③：重叠不叠加 的实现载体）。
    ///
    /// 缓存键 = (形状版本号, 画布宽, 画布高)。缩放/平移**不在键里** ——
    /// 它们由 <c>Render</c> 外层的 <c>PushTransform</c> 施加，不影响离屏位图内容。
    ///
    /// 失效方式：任何改动 <c>_shapes</c> / <c>_current</c> 的地方调用
    /// <see cref="InvalidateFlatLayer"/>（版本号 +1）。**拖动中的 `_current` 不参与缓存**
    /// （它每帧都变，由 <c>Render</c> 直接叠加绘制）。
    /// </summary>
    private Avalonia.Media.Imaging.RenderTargetBitmap? _flatLayer;
    private int _flatVersion = -1;
    private int _flatW = -1;
    private int _flatH = -1;
    private int _shapeVersion;

    /// <summary>标记集合发生了变化 → 让离屏缓存失效（下次 Render 重建）。</summary>
    private void InvalidateFlatLayer() => _shapeVersion++;

    /// <summary>
    /// 取（必要时重建）"扁平层"：把 <c>_shapes</c> 全部画到一张**不透明**位图上。
    /// 重叠处在这一步已被压成纯色 → 之后整层以固定 alpha 贴回时不会加深。
    /// </summary>
    private Avalonia.Media.Imaging.RenderTargetBitmap EnsureFlatLayer(double cw, double ch)
    {
        var pw = Math.Max(1, (int)Math.Ceiling(cw));
        var ph = Math.Max(1, (int)Math.Ceiling(ch));

        if (_flatLayer is not null && _flatVersion == _shapeVersion
            && _flatW == pw && _flatH == ph)
            return _flatLayer;

        var fresh = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(pw, ph), new Vector(96, 96));
        var minSide = Math.Min(cw, ch);
        using (var octx = fresh.CreateDrawingContext())
            foreach (var sh in _shapes) sh.DrawPreview(octx, cw, ch, minSide);

        var old = _flatLayer;
        _flatLayer = fresh;
        _flatVersion = _shapeVersion;
        _flatW = pw;
        _flatH = ph;
        old?.Dispose();
        return fresh;
    }

    // ---------------------------------------------------------------- 编辑历史（需求②）
    /// <summary>
    /// 标注编辑的撤销栈 / 重做栈（**按动作顺序**记录，用户需求②）。
    ///
    /// 每次"完成一次编辑动作"（落笔成形状 / 擦除 / 清除 / 导入）后推一份快照。
    /// 快照用 <see cref="RegionSnapshot"/>（就是导出/导入用的结构）→ 无需另设序列化。
    /// 上限 <see cref="MaxHistory"/> 防止长时间编辑吃掉内存（超出丢最旧的）。
    /// </summary>
    private const int MaxHistory = 100;
    private readonly List<RegionSnapshot> _undoStack = new();
    private readonly List<RegionSnapshot> _redoStack = new();

    /// <summary>可撤销的动作数（0 = 无）。</summary>
    public int UndoDepth => _undoStack.Count;

    /// <summary>可重做的动作数（0 = 无）。</summary>
    public int RedoDepth => _redoStack.Count;

    /// <summary>编辑历史变化（撤销/重做可用性变化）时触发，供 UI 更新按钮可用性。</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>把**当前状态**压入撤销栈，并清空重做栈（新的编辑动作会打断重做链）。</summary>
    private void PushHistory()
    {
        _undoStack.Add(ExportShapes());
        if (_undoStack.Count > MaxHistory) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>撤销：回到上一个动作之前的状态。返回是否真的撤销了（无历史时 false）。</summary>
    public bool UndoEdit()
    {
        if (_undoStack.Count == 0) return false;
        var last = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        // 把"当前状态"放进重做栈（这样重做能回到撤销前）
        _redoStack.Add(ExportShapes());
        ReplaceShapes(last);   // ⚠️ 用 ReplaceShapes：不能再次推历史（否则撤销栈永不减少）
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>重做：重新应用被撤销的动作。返回是否真的重做了。</summary>
    public bool RedoEdit()
    {
        if (_redoStack.Count == 0) return false;
        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(ExportShapes());
        ReplaceShapes(next);   // 同上：不推历史
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 视图变换（缩放 + 平移）变化时触发。
    /// 底图 `Image` 需订阅它并套用同一矩阵 —— 否则缩放只有标注在动，
    /// 用户看到的是「缩放/复位完全不起效」（v5.25.0 修复的真实缺陷）。
    /// </summary>
    public event EventHandler? ViewChanged;

    /// <summary>当前视图缩放倍数（1.0 = 原始适配）。</summary>
    public double Zoom => _zoom;

    /// <summary>
    /// 视图变换的基准尺寸 = **图片矩形**的宽高（不是控件尺寸）。
    ///
    /// ⚠️ 为什么用图片矩形而不是控件尺寸（v0.5.34 用户澄清的语义）：
    ///   · 控件 Bounds = 整个预览灰区（可**查看**范围，不裁标注）；
    ///   · 坐标基准 = 图片矩形（可**绘制**范围）。
    ///   若用控件尺寸当基准，图片在容器里居中留白时缩放中心会跑到灰区中心 —— 又偏了。
    /// </summary>
    private Size ViewSize
    {
        get
        {
            var r = ContentRect;
            return r.Width > 0 && r.Height > 0
                ? new Size(r.Width, r.Height)
                : new Size(1, 1);
        }
    }

    /// <summary>
    /// 构建视图矩阵：绕**图片中心**缩放 + **屏幕像素**平移，并带上图片在控件中的原点偏移。
    ///
    /// <c>screen = origin + (local − c/2)·z + c/2 + pan</c>（<c>c</c> = 图片尺寸，
    /// <c>origin</c> = 图片在控件内的左上角）。
    ///
    /// 为什么必须带 <c>origin</c>：控件铺满灰区、图片在其中居中，所以"图片局部坐标 → 控件坐标"
    /// 需要加这一步。`Image` 自身已被布局放在 <c>origin</c>，故图片层用
    /// <see cref="ImageMatrix"/>（不含 origin），避免偏移两次。
    /// </summary>
    private Matrix BuildMatrix()
    {
        var r = ContentRect;
        return Matrix.CreateTranslation(-r.Width / 2, -r.Height / 2)
             * Matrix.CreateScale(_zoom, _zoom)
             * Matrix.CreateTranslation(r.Width / 2, r.Height / 2)
             * Matrix.CreateTranslation(_panX, _panY)
             * Matrix.CreateTranslation(r.X, r.Y);   // ← origin 放最右（输入是图片局部坐标）
    }

    /// <summary>
    /// 给底图 `Image` 用的矩阵：**不含**图片原点偏移。
    ///
    /// 因为 `Image` 已被布局放在图片矩形的左上角，Avalonia 会自己加上那一步。
    /// 关系：<c>ViewMatrix == ImageMatrix · T(contentX, contentY)</c>。
    /// 用法：`image.RenderTransformOrigin = RelativePoint.TopLeft` + 本矩阵。
    /// </summary>
    public Matrix ImageMatrix
    {
        get
        {
            var r = ContentRect;
            return Matrix.CreateTranslation(-r.Width / 2, -r.Height / 2)
                 * Matrix.CreateScale(_zoom, _zoom)
                 * Matrix.CreateTranslation(r.Width / 2, r.Height / 2)
                 * Matrix.CreateTranslation(_panX, _panY);
        }
    }

    /// <summary>
    /// 当前视图矩阵（**与 <see cref="Render"/> 内部使用的完全一致**）。
    /// 底图 `Image` 直接套用同一个矩阵即可（先把 `RenderTransformOrigin` 设为 TopLeft）。
    /// </summary>
    public Matrix ViewMatrix => BuildMatrix();

    /// <summary>把当前视图变换广播给订阅者（底图同步用）。</summary>
    private void RaiseViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);

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

    /// <summary>
    /// 导出蒙版时把笔触**放大**的倍数（见 FreeStroke.RenderToMaskFilled 的说明）。
    /// 3 倍：默认 3px 的细笔 → 9px 的区域，对模型才是"可辨认的改动区域"。
    /// </summary>
    internal const double MaskStrokeBoost = 3.0;

    /// <summary>导出蒙版时的最小笔宽比例（最短边的百分比）→ 防止细笔在 4K 图上变成一条缝。</summary>
    internal const double MaskMinStrokeRatio = 0.012;

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
        // ⚠️ **必须 false**（v0.5.34，用户报"缩放后标注上面部分消失"）：
        //   本控件铺满预览灰区，图片只是其中居中的一块。标注在缩放/平移后
        //   会**超出图片范围**（这是正常的 —— 用户画的就是图片外的位置，
        //   或者图片缩小后原本在图内的标注跑到图外）。
        //   若这里裁剪，那部分标注会**凭空消失**。
        //   可查看范围由父级 `<Border ClipToBounds="True">`（= 预览灰区）界定，
        //   那才是用户期望的"可见边界"。
        ClipToBounds = false;
    }

    /// <summary>
    /// 尺寸变化时重建视图矩阵并广播（③ 相关）。
    ///
    /// <see cref="BuildMatrix"/> 以 <c>Bounds.Width/Height</c> 为中心基准 ——
    /// 窗口 resize / 宽窄布局切换会改变 Bounds，此时旧的 <c>RenderTransform</c>
    /// 已按旧尺寸算好，若不同步就会与标注层错位。
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RaiseViewChanged();
    }

    public void ResetView()
    {
        _zoom = 1.0; _panX = 0; _panY = 0;
        InvalidateVisual();
        RaiseViewChanged();
    }

    /// <summary>
    /// 仅供测试：直接设定视图状态。
    ///
    /// 为什么需要：平移只能通过指针事件驱动，而单测环境没有输入源 ——
    /// 若不暴露这个入口，测试就只能**复刻**矩阵公式来断言，
    /// 那样一旦生产代码改了公式，测试却仍按旧公式通过（假绿）。
    /// 有了它，测试可以直接断言生产代码真实的 <see cref="ViewMatrix"/>。
    /// </summary>
    internal void SetViewForTest(double zoom, double panX, double panY)
    {
        _zoom = Math.Clamp(zoom, 0.2, 8.0);
        _panX = panX; _panY = panY;
        InvalidateVisual();
        RaiseViewChanged();
    }

    /// <summary>
    /// 仅供测试：当前平移量（屏幕像素）。
    /// 平移只能经指针事件驱动，单测没有输入源 —— 暴露只读入口，
    /// 避免测试用反射读私有字段（那样改字段名就会静默失效）。
    /// </summary>
    internal (double PanX, double PanY) ViewPanForTest => (_panX, _panY);

    /// <summary>
    /// 仅供测试：设置"指针位置"（控件坐标）。
    ///
    /// 为什么需要：笔刷**指示圈**只在指针位于图片内时绘制，而单测环境没有真实指针输入 ——
    /// 不暴露这个入口就只能靠截图肉眼看（不可靠、不可回归）。
    /// </summary>
    internal void SetPointerForTest(Point p)
    {
        _lastPointer = p;
        InvalidateVisual();
    }

    /// <summary>
    /// 仅供测试：在**当前指针位置**执行一次擦除。
    /// 用于验证"指示圈半径 == 真实擦除半径"（需求⑦）这一不变量。
    /// </summary>
    internal void EraseAtPointerForTest()
    {
        if (_lastPointer is not { } p) return;
        var (nx, ny) = ToNormalized(p);
        EraseAt(nx, ny);
    }

    // ---------------------------------------------------------------- 图片内容矩形（坐标基准）

    /// <summary>
    /// **图片在控件内的矩形**（`Stretch="Uniform"` 后图片实际占据的区域）。
    ///
    /// ⚠️ 这是坐标基准，与"控件可见范围"是**两件事**（v0.5.34 用户澄清）：
    ///   · **可绘制范围** = 图片尺寸 → 归一化坐标以本矩形为基准，落在 [0,1] 之外的不采纳；
    ///   · **可查看范围** = 整个预览灰区 → 控件 Bounds 铺满容器，**不按图片裁剪**，
    ///     这样缩放/平移后标注超出图片的部分依然可见（旧实现把控件尺寸设成图片尺寸 →
    ///     `ClipToBounds` 把超出的标注裁掉，用户看到"上面的部分消失了"）。
    ///
    /// 默认 <c>default</c>（宽/高为 0）= 未设置 → 退回"整个控件"。
    /// </summary>
    public static readonly StyledProperty<Rect> ImageContentRectProperty =
        AvaloniaProperty.Register<RegionCanvas, Rect>(nameof(ImageContentRect), default);

    public Rect ImageContentRect
    {
        get => GetValue(ImageContentRectProperty);
        set => SetValue(ImageContentRectProperty, value);
    }

    /// <summary>实际使用的图片矩形：未设置（宽/高为 0）时退回整个控件。</summary>
    private Rect ContentRect
    {
        get
        {
            var r = ImageContentRect;
            if (r.Width > 0 && r.Height > 0) return r;
            return new Rect(0, 0, Bounds.Width, Bounds.Height);
        }
    }

    static RegionCanvas()
    {
        // 图片矩形变化 → 坐标基准与变换中心都变了 → 重绘并广播（否则图片层停在旧矩阵上）
        AffectsRender<RegionCanvas>(ImageContentRectProperty);
        ImageContentRectProperty.Changed.AddClassHandler<RegionCanvas>((c, _) =>
        {
            c.InvalidateVisual();
            c.RaiseViewChanged();
        });
    }

    /// <summary>
    /// 计算「以 <paramref name="anchor"/> 为锚点缩放」后的新平移量（纯函数，便于单测）。
    ///
    /// <c>screen = origin + (local − c/2)·z + c/2 + pan</c>
    /// （<c>c</c> = 图片尺寸，<c>origin</c> = 图片在控件内的左上角，pan 为屏幕像素）。
    /// 令 <c>r = z'/z</c>、锚点相对**图片中心**的偏移 <c>d = anchor − origin − c/2</c>。
    /// 要求锚点下的图像点不动：<c>(d − pan)/z = (d − pan')/z'</c>
    /// ⇒ <b><c>pan' = pan·r + d·(1 − r)</c></b>。
    /// </summary>
    internal static (double PanX, double PanY) ComputeZoomPan(
        double panX, double panY, double oldZoom, double newZoom,
        double width, double height, Point? anchor,
        double originX = 0, double originY = 0)
    {
        if (anchor is not { } a || width <= 0 || height <= 0
            || oldZoom <= 0 || newZoom <= 0)
            return (panX, panY);   // 无锚点（或尺寸未就绪）→ 居中缩放

        var r = newZoom / oldZoom;
        var dx = a.X - originX - width / 2;
        var dy = a.Y - originY - height / 2;
        return (panX * r + dx * (1 - r), panY * r + dy * (1 - r));
    }

    /// <summary>
    /// 控件坐标 → 图片局部坐标（与 <see cref="BuildMatrix"/> 严格互为逆）。
    /// <c>local = (screen − origin − c/2 − pan)/zoom + c/2</c>
    /// </summary>
    internal static (double X, double Y) ScreenToImage(
        Point screen, double zoom, double panX, double panY,
        double width, double height, double originX = 0, double originY = 0)
    {
        if (zoom <= 0) return (screen.X - originX, screen.Y - originY);
        return ((screen.X - originX - width / 2 - panX) / zoom + width / 2,
                (screen.Y - originY - height / 2 - panY) / zoom + height / 2);
    }

    /// <summary>以控件中心为锚点缩放（按钮/快捷键路径）。</summary>
    public void ZoomBy(double factor) => ZoomBy(factor, null);

    /// <summary>
    /// 缩放。传入 <paramref name="anchor"/>（滚轮路径传鼠标位置）时，
    /// **该点下的图像内容保持不动** —— 即"以鼠标所在点为原点放大缩小"。
    /// </summary>
    /// <param name="factor">缩放倍数（&gt;1 放大，&lt;1 缩小）。</param>
    /// <param name="anchor">锚点的控件坐标；null = 控件中心。</param>
    public void ZoomBy(double factor, Point? anchor)
    {
        var nz = Math.Clamp(_zoom * factor, 0.2, 8.0);
        if (Math.Abs(nz - _zoom) < 1e-9) return;

        var r = ContentRect;
        (_panX, _panY) = ComputeZoomPan(
            _panX, _panY, _zoom, nz, r.Width, r.Height, anchor, r.X, r.Y);

        _zoom = nz;
        InvalidateVisual();
        RaiseViewChanged();
    }

    private Color EffectiveColor => Color.TryParse(BrushColor, out var c) ? c : Colors.Red;

    /// <summary>
    /// 控件坐标 → 图片**局部**坐标（逆变换）。
    /// 与 <see cref="BuildMatrix"/> 严格互为逆：<c>local = (p − origin − c/2 − pan)/z + c/2</c>。
    /// </summary>
    private (double X, double Y) ToLocal(Point p)
    {
        var r = ContentRect;
        return ScreenToImage(p, _zoom, _panX, _panY, r.Width, r.Height, r.X, r.Y);
    }

    /// <summary>
    /// 控件坐标 → 图片归一化坐标（0~1，以图片矩形为基准）。
    /// 落在 [0,1] 之外表示点不在图片上。
    /// </summary>
    private (double Nx, double Ny) ToNormalized(Point p)
    {
        var r = ContentRect;
        if (r.Width <= 0 || r.Height <= 0) return (0, 0);
        var (x, y) = ToLocal(p);
        return (x / r.Width, y / r.Height);
    }

    /// <summary>
    /// 控件坐标 → 图片归一化坐标，并**钳制到图片范围内**（v0.5.35）。
    ///
    /// ⚠️ 为什么必须钳制（用户澄清的语义）：
    ///   · **可绘制范围 = 图片尺寸** —— 用户在图片外的灰区落笔时，标记必须被**限制在图片边缘**，
    ///     而不是记下超范围的坐标（那样矩形会画到灰区里去，送服务端时也是越界的无效区域）；
    ///   · **可查看范围 = 整个灰区** —— 这只影响"看得见什么"，不影响"能画到哪"。
    ///   两者是不同的事，不要混。
    /// </summary>
    private (double Nx, double Ny) ToNormalizedClamped(Point p)
    {
        var (nx, ny) = ToNormalized(p);
        return (Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
    }

    /// <summary>点是否落在**图片范围内**（用于判断"是否该开始一次绘制"）。</summary>
    private bool IsInsideImage(Point p)
    {
        var (nx, ny) = ToNormalized(p);
        return nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1;
    }

    /// <summary>归一化线宽（与缩放无关 → 缩放后视觉粗细一致）。基准 = 图片尺寸。</summary>
    private double NormSize()
    {
        var r = ContentRect;
        var minSide = Math.Max(1, Math.Min(r.Width, r.Height));
        var basePx = BrushSize <= 0 ? minSide * 0.03 : BrushSize;
        return Math.Clamp(basePx / minSide, 0.002, 0.5);
    }

    /// <summary>
    /// 橡皮**归一化半径**（唯一真源）。
    ///
    /// ⚠️ v0.5.36 需求⑦：此前两处基准不同 ——
    ///   指示圆圈用 <c>BrushSize*1.2</c>（**屏幕像素、不随缩放**），
    ///   而实际擦除用 <c>NormSize()*2.5</c>（**归一化空间**）→ 两者差一个 zoom 与尺寸换算，
    ///   实测擦除范围明显大于画出的圆圈。
    ///   现在统一到本属性：擦除判定与指示圈都从它派生。
    /// </summary>
    private double EraserNormRadius()
    {
        var c = ContentRect;
        var minSide = Math.Max(1, Math.Min(c.Width, c.Height));
        var basePx = BrushSize <= 0 ? 20 : BrushSize * 1.2;
        // 半径是"图片上的长度"→ 用归一化表示（minSide 是最短边像素数）
        return Math.Max(6, basePx) / minSide;
    }

    /// <summary>
    /// 马克笔笔迹的归一化半径（与绘制笔宽同源，需求⑧的指示圈用）。
    ///
    /// ⚠️ `NormSize()` 返回的是**归一化直径**（绘制时当 pen 宽度用），
    ///   所以半径 = 它的一半 —— 这样指示圈正好套住即将画出的笔迹。
    /// </summary>
    private double MarkerNormRadius() => NormSize() / 2.0;

    // ---------------------------------------------------------------- 渲染
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var content = ContentRect;
        if (content.Width <= 0 || content.Height <= 0) return;

        // ⚠️ 关键：先填充一个透明矩形。
        // Control 基类没有 Background 属性，而 Avalonia 的命中测试基于**已绘制的几何**：
        // 没有绘制 → 指针事件穿透到下层 Image → 标注根本画不上。
        // 填**整个控件**（= 预览灰区）→ 灰区内都能落笔（可绘制范围由 ToNormalized 限制）。
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        // 变换：绕图片中心缩放 + 屏幕像素平移 + 图片原点偏移。
        // 形状用**图片局部坐标**（0..imgW, 0..imgH）绘制，偏移由矩阵统一施加。
        using (ctx.PushTransform(BuildMatrix()))
        {
            var cw = content.Width; var ch = content.Height;
            var minSide = Math.Min(cw, ch);

            // ⚠️ v0.5.36 需求③：**重叠区域不叠加**。
            //   旧实现用 <c>PushOpacity</c> 只是给整组套了统一不透明度，
            //   但组内多个形状在重叠处**仍会 alpha 累加**（同色叠加会更深）。
            //   现在改成**离屏合成**（与 <c>ExportComposite</c> 同一套语义）：
            //     ① 先把所有标记画到一张**不透明**的临时层（重叠处仍是纯色）；
            //     ② 再把该层整体以固定 alpha 贴到画布上。
            //   这样"同一条内不叠加、跨条也不叠加"，且**预览与导出的观感完全一致**。
            var alpha = Math.Clamp(LayerAlpha, 10, 255) / 255.0;
            if (_shapes.Count == 0 && _current is null)
            {
                // 无标记 → 什么都不画
            }
            else
            {
                // ⚠️ v0.5.38（性能）：离屏层**缓存**，不再每帧新建。
                //   原先每帧 `new RenderTargetBitmap(cw × ch)` —— 拖动时（~60fps）
                //   每秒分配 60 张与图片同尺寸的位图（4K 图约 33MB/张）→ GC 抖动。
                //   位图内容只取决于「形状集合 + 画布尺寸」，与缩放/平移无关
                //   （后者由外层 PushTransform 施加）→ 正好按这两个键缓存。
                //   拖动中的 `_current` **不进缓存**（每帧都在变）：它单独叠加绘制，
                //   而且它自己是一条形状，不参与"跨形状重叠压平"。
                var flat = EnsureFlatLayer(cw, ch);
                // 整层以固定 alpha 贴回（重叠处已被压成纯色 → 不会加深）
                using (ctx.PushOpacity(alpha))
                {
                    ctx.DrawImage(flat, new Rect(0, 0, cw, ch));

                    // 拖动中的那条：**必须也在同一个 PushOpacity 内**。
                    // ⚠️ v0.5.38 回归修复：上一版把它移到了 PushOpacity 外面 →
                    //   拖动时那条是**不透明**的，一松手（进入 _shapes 走离屏层）才变半透明，
                    //   观感上"画的时候实、松手变淡"，用户直接报了出来。
                    //   它不进离屏层缓存（每帧都在变），但要共享同一个 alpha。
                    _current?.DrawPreview(ctx, cw, ch, minSide);
                }
            }
        }

        // ⚠️ v0.5.36 需求⑦⑧：笔刷指示标记。
        //   必须画在**与擦除判定同一坐标系**里 —— 擦除半径是归一化的（以最短边为基准），
        //   映射到屏幕时会乘 zoom，所以指示圈也套同一个矩阵（否则两者又会不一致）。
        //   注意用**最短边**算半径（与 NormSize/EraserNormRadius 的基准一致），
        //   否则宽高比不同时指示圈会变成椭圆。
        //
        // ⚠️ v0.5.39 用户反馈："马克笔的提示标记能否换一个简洁一点的，粗细调很小的时候显示异常"。
        //   根因：马克笔半径 = 笔宽 / 2，笔宽调到最小时半径只有几像素，
        //   而**虚线圆**在这么小的尺寸上会糊成一团色块（不是清晰的圆）。
        //   改法：马克笔不再画圆，改用**十字准星**（与粗细无关、永远清晰、更简洁）；
        //        橡皮仍画圆（它表达"擦除范围"，这个语义必须保留），并加**最小半径保护**。
        if (_lastPointer is { } lp && IsInsideImage(lp))
        {
            var (inx, iny) = ToNormalized(lp);
            var isEraser = Tool == RegionTool.Eraser;
            var isMarker = Tool == RegionTool.Marker;
            if (isEraser || isMarker)
            {
                var c = ContentRect;
                var px = inx * c.Width;
                var py = iny * c.Height;
                using (ctx.PushTransform(BuildMatrix()))
                {
                    if (isMarker)
                    {
                        // 十字准星：尺寸固定为**屏幕可见**大小（除以 zoom 换算回局部坐标 →
                        // 缩放画布时准星不会跟着变大变小，像鼠标指针一样稳定）。
                        var inv = 1.0 / Math.Max(0.05, _zoom);
                        var len = 9.0 * inv;
                        var brush = new SolidColorBrush(Color.FromArgb(235, 255, 230, 120));
                        var crossPen = new Pen(brush, 1.5 * inv);
                        ctx.DrawLine(crossPen, new Point(px - len, py), new Point(px + len, py));
                        ctx.DrawLine(crossPen, new Point(px, py - len), new Point(px, py + len));
                        // 中心实心小点：标出精确落点（也保证极小笔宽时仍看得见）
                        ctx.DrawEllipse(brush, null, new Point(px, py), 1.5 * inv, 1.5 * inv);
                    }
                    else
                    {
                        var minSide = Math.Max(1, Math.Min(c.Width, c.Height));
                        var rPx = EraserNormRadius() * minSide;
                        // 最小半径保护：太小时虚线会糊成一团 → 退化为实线小圆
                        var dash = rPx >= 6 ? DashStyle.Dash : null;
                        var inv = 1.0 / Math.Max(0.05, _zoom);
                        var pen = new Pen(
                            new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1.5 * inv, dashStyle: dash);
                        ctx.DrawEllipse(null, pen, new Point(px, py), rPx, rPx);
                    }
                }
            }
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

        // ⚠️ v0.5.35：**可绘制范围 = 图片**。在图片外的灰区按下时不落笔
        //（否则会记下越界坐标 → 矩形画到灰区里 / 送服务端是无效区域）。
        if (!IsInsideImage(p)) return;

        var (nx, ny) = ToNormalizedClamped(p);

        if (Tool == RegionTool.Eraser)
        {
            PushHistory();   // 需求②：一次"擦除"（含连续拖动）= 一个可撤销动作
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
            RaiseViewChanged();   // 底图同步平移
            return;
        }

        if (Tool == RegionTool.Eraser)
        {
            if (_current is null && Bounds.Width > 0)
            {
                var (ex, ey) = ToNormalizedClamped(p);   // v0.5.35：擦除也限制在图片内
                EraseAt(ex, ey);
            }
            InvalidateVisual();
            return;
        }

        if (_current is null) { InvalidateVisual(); return; }
        // v0.5.35：拖动时坐标同样钳制 → 标记不会超出图片边界
        //（用户在图片外继续拖，形状停在图片边缘，而不是画到灰区里）
        var (nx, ny) = ToNormalizedClamped(p);

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
                _current = null;      // 太小的框视为误触，不成立也不记历史
            else
            {
                PushHistory();        // 需求②：一次"落笔成形状"= 一个可撤销动作
                _shapes.Add(_current); _current = null;
                InvalidateFlatLayer();   // v0.5.38：离屏缓存失效
            }
        }
        e.Pointer.Capture(null);
        InvalidateVisual();
        RegionCountChanged?.Invoke(this, _shapes.Count);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // ① 以鼠标所在点为锚点缩放（该点下的图像内容保持不动），而不是固定绕控件中心
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this));
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
        // ⚠️ v0.5.36 需求⑦：用与**指示圆圈同一个真源**的半径（此前是 NormSize()*2.5，
        //   与画出来的圆圈不一致 → 擦除范围明显大于圆圈）。
        var r = EraserNormRadius();
        if (_shapes.RemoveAll(sh => sh.HitTest(nx, ny, r)) > 0)
        {
            InvalidateFlatLayer();   // v0.5.38：离屏缓存失效
            InvalidateVisual();
            RegionCountChanged?.Invoke(this, _shapes.Count);
        }
    }

    public void ClearRegions()
    {
        if (_shapes.Count > 0) PushHistory();   // 需求②：清除可撤销（空的时候不必记）
        _shapes.Clear(); _current = null;
        InvalidateFlatLayer();   // v0.5.38：离屏缓存失效
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

    /// <summary>
    /// 用快照替换当前形状（用户可见的"载入"路径）—— **会记录历史**。
    /// 用于：切图恢复标注、导入外部快照。
    /// </summary>
    public void ImportShapes(RegionSnapshot? snap)
    {
        PushHistory();          // 记录"载入前"的状态，使载入也可撤销
        ReplaceShapes(snap);
    }

    /// <summary>导出当前形状（供缓存/跨实例搬运）。</summary>
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

    /// <summary>
    /// 用快照替换当前形状。**不记录历史**（调用方决定是否记）。
    ///
    /// ⚠️ 之所以拆出这个内部方法：<see cref="ImportShapes"/>（用户可见的"载入"）
    /// 与 <see cref="UndoEdit"/><see cref="RedoEdit"/> 都走它，
    /// 但只有前者该推历史 —— 否则撤销操作本身会污染撤销栈（永远撤不完）。
    /// </summary>
    private void ReplaceShapes(RegionSnapshot? snap)
    {
        _shapes.Clear(); _current = null;
        InvalidateFlatLayer();   // v0.5.38：离屏缓存失效（撤销/重做/导入都走这里）
        if (snap is not null)
        {
            foreach (var d in snap.Strokes)
            {
                var color = Color.FromRgb((byte)d.R, (byte)d.G, (byte)d.B);
                if (d.IsOutline)
                {
                    // v0.5.35：导入时钳制到图片范围 —— 旧快照可能含越界坐标
                    //（v0.5.34 之前允许在图片外落笔），不钳制会让框重新画到灰区里。
                    _shapes.Add(new OutlineShape
                    {
                        Color = color, Size = d.Size, IsEllipse = d.IsEllipse,
                        X0 = Math.Clamp(d.X0, 0, 1), Y0 = Math.Clamp(d.Y0, 0, 1),
                        X1 = Math.Clamp(d.X1, 0, 1), Y1 = Math.Clamp(d.Y1, 0, 1),
                    });
                }
                else
                {
                    var fs = new FreeStroke { Color = color, Size = d.Size };
                    for (int i = 0; i + 1 < d.Points.Count; i += 2)
                        fs.Points.Add((Math.Clamp(d.Points[i], 0, 1),
                                       Math.Clamp(d.Points[i + 1], 0, 1)));
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

        // 标记先画到不透明的 mask，再整体以「原色 + 固定 alpha」贴回 → 重复涂抹不加深。
        // ⚠️ alpha 施加用颜色矩阵（A' = a×A），不用逐像素循环：
        //    4K 图 829 万像素的 GetPixel/SetPixel 会卡住 UI 数秒。
        byte alpha = (byte)Math.Clamp(LayerAlpha, 10, 255);
        float a = alpha / 255f;
        using var mask = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(mask))
        {
            canvas.Clear(SKColors.Transparent);
            foreach (var sh in _shapes) sh.RenderToMask(canvas, w, h);
            canvas.Flush();
        }
        using (var paint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(new[]
            {
                1, 0, 0, 0, 0,      // R' = R
                0, 1, 0, 0, 0,      // G' = G
                0, 0, 1, 0, 0,      // B' = B
                0, 0, 0, a, 0,      // A' = a × A
            }),
            IsAntialias = false,
        })
        {
            surface.Canvas.DrawBitmap(mask, 0, 0, paint);
        }

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 95);
        return png.ToArray();
    }

    /// <summary>
    /// 单独导出「修改区域蒙版」PNG，**供支持 mask/inpainting 的模型使用**。
    ///
    /// ⚠️ 语义必须与官方文档一致（否则蒙版"发出去了但完全不生效"）：
    ///   文档（APIMart mask_url）要求：
    ///     · 使用**带 Alpha 透明通道**的 PNG
    ///     · **alpha = 0（完全透明）的区域 = 需要修改**，其余区域用于保留
    ///     · 普通黑白图**不能**当蒙版（服务端不会自动补 Alpha）
    ///   所以这里导出的是「透明底 + 不透明彩色标记」：
    ///     被圈画的像素 alpha=255（保留语义上"要被替换"，但按文档是要 **透明** 才表示要改）。
    ///
    ///   注意方向：文档说"透明=要改"，因此**标记区域必须是透明的**。
    ///   实现上先按"标记=不透明"画出来，再整体反相 alpha —— 这样画笔/方框/圆圈的
    ///   渲染逻辑（含抗锯齿边缘）得以复用，只需要最后翻转一次即可。
    ///
    ///   ⚠️ 与旧实现的差别（v5.27.0 修复）：
    ///     旧版用**黑底 + 白色线描**（`Clear(SKColors.Black)` + 描边不填充），
    ///     既没有 Alpha 通道、又把"要改的区域"画成了**只有边框**而非整个区域 ——
    ///     对模型来说等于"没有明确的可改区域"，这是蒙版链路失效的直接原因。
    ///     现在改为**填充**（矩形/椭圆填充 + 笔画），符合"区域"语义。
    /// </summary>
    public byte[]? ExportMask()
    {
        if (_shapes.Count == 0) return null;
        var path = BaseImagePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var baseBmp = SKBitmap.Decode(path);
        if (baseBmp is null) return null;

        int w = baseBmp.Width, h = baseBmp.Height;

        // ① 先按"标记 = 不透明"渲染到一张全透明的位图上
        using var marked = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(marked))
        {
            canvas.Clear(SKColors.Transparent);
            foreach (var sh in _shapes) sh.RenderToMaskFilled(canvas, w, h);
            canvas.Flush();
        }

        // ② 反相 alpha：标记处 → 0（透明 = 要改），其余 → 255（保留），
        //    同时把 RGB 归零（蒙版语义只在 Alpha 上，避免不同实现按 RGB 解读产生歧义）。
        //
        // ⚠️ 这两件事必须用 **SKColorFilter 颜色矩阵**一次性完成，不能逐像素做：
        //    · 逐像素 GetPixel/SetPixel：4K 图有 829 万像素 → 800 万次托管调用（卡 UI 数秒）；
        //    · 用 SKBlendMode.Difference 看似更直观，但**实测语义是错的**：
        //      Difference 对 alpha 走的是并集公式（union），标记区 alpha 仍是 255
        //      → 蒙版变成"整张不透明"，等于没指定可改区域（这正是旧实现失效的同类问题）。
        //    颜色矩阵里 A' = -1×A + 1 才是真正的按像素反相，且 RGB 全零由矩阵系数保证。
        using var outBmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(outBmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var filter = SKColorFilter.CreateColorMatrix(AlphaInvertMatrix);
            using var paint = new SKPaint { ColorFilter = filter, IsAntialias = false };
            canvas.DrawBitmap(marked, 0, 0, paint);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(outBmp);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>
    /// 颜色矩阵：R/G/B → 0，Alpha → 255 - Alpha。
    /// 形如 Skia 期望的 4×5 行主序（A' 的系数在 [3,3]，常量偏移在 [3,4]，偏移单位 = 1.0 即 255）。
    /// </summary>
    private static readonly float[] AlphaInvertMatrix =
    {
        0, 0, 0, 0, 0,      // R' = 0
        0, 0, 0, 0, 0,      // G' = 0
        0, 0, 0, 0, 0,      // B' = 0
        0, 0, 0, -1, 1,     // A' = 255 - A
    };
}
