using Avalonia;
using Avalonia.Controls;
using Avalonia.Skia;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 标注编辑**动作历史**（撤销 / 重做）的回归（用户需求②）。
///
/// 语义：每一次「落笔成形状 / 擦除一次 / 清除 / 载入快照」算**一个动作**，
/// 可以按**顺序**逐步撤销，也能重做回来。栈上限 100（防止长时间编辑吃内存）。
/// </summary>
public class RegionCanvasHistoryTests
{
    static RegionCanvasHistoryTests() => SkiaPlatform.Initialize();

    private static ImgHub.App.Controls.RegionCanvas Build()
    {
        var canvas = new ImgHub.App.Controls.RegionCanvas
        { BrushColor = "#FF0000", BrushSize = 20 };
        var root = new Grid { Width = 300, Height = 300 };
        root.Children.Add(canvas);
        root.Measure(new Size(300, 300));
        root.Arrange(new Rect(0, 0, 300, 300));
        canvas.ImageContentRect = new Rect(0, 0, 300, 300);
        return canvas;
    }

    /// <summary>直接加一个矩形（不走指针事件）——模拟"完成一次绘制动作"。</summary>
    private static void DrawRect(ImgHub.App.Controls.RegionCanvas c,
                                 double x0, double y0, double x1, double y1)
    {
        // ImportShapes 是公开的"载入"路径（会推历史）→ 用它模拟一次编辑动作
        var snap = c.ExportShapes();
        snap.Strokes.Add(new ImgHub.App.Controls.RegionCanvas.StrokeData
        {
            R = 255, G = 0, B = 0, Size = 0.02,
            IsOutline = true, IsEllipse = false,
            X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
        });
        c.ImportShapes(snap);
    }

    [Fact]
    public void NewCanvas_HasNoHistory()
    {
        var c = Build();
        Assert.Equal(0, c.UndoDepth);
        Assert.Equal(0, c.RedoDepth);
        Assert.False(c.UndoEdit(), "无历史时撤销应返回 false（UI 据此不误报）");
        Assert.False(c.RedoEdit(), "无历史时重做应返回 false");
    }

    [Fact]
    public void Undo_RemovesLastAction_AndRedoBringsItBack()
    {
        var c = Build();
        DrawRect(c, 0.1, 0.1, 0.3, 0.3);      // 动作 1
        Assert.Equal(1, c.RegionCount);

        DrawRect(c, 0.5, 0.5, 0.8, 0.8);      // 动作 2
        Assert.Equal(2, c.RegionCount);
        Assert.True(c.UndoDepth > 0);

        // 撤销 → 回到只有 1 个
        Assert.True(c.UndoEdit());
        Assert.Equal(1, c.RegionCount);

        // 重做 → 又变回 2 个
        Assert.True(c.RedoEdit());
        Assert.Equal(2, c.RegionCount);
    }

    [Fact]
    public void Undo_CanStepBackThroughMultipleActions_InOrder()
    {
        // 按**动作顺序**逐步回退：3 → 2 → 1 → 0
        var c = Build();
        DrawRect(c, 0.10, 0.10, 0.20, 0.20);
        DrawRect(c, 0.30, 0.30, 0.40, 0.40);
        DrawRect(c, 0.50, 0.50, 0.60, 0.60);
        Assert.Equal(3, c.RegionCount);

        Assert.True(c.UndoEdit()); Assert.Equal(2, c.RegionCount);
        Assert.True(c.UndoEdit()); Assert.Equal(1, c.RegionCount);
        Assert.True(c.UndoEdit()); Assert.Equal(0, c.RegionCount);
        Assert.False(c.UndoEdit());   // 已到最底
    }

    [Fact]
    public void Clear_IsUndoable()
    {
        var c = Build();
        DrawRect(c, 0.1, 0.1, 0.3, 0.3);
        DrawRect(c, 0.5, 0.5, 0.7, 0.7);
        Assert.Equal(2, c.RegionCount);

        c.ClearRegions();
        Assert.Equal(0, c.RegionCount);

        Assert.True(c.UndoEdit(), "清除必须可撤销");
        Assert.Equal(2, c.RegionCount);
    }

    [Fact]
    public void Clear_OnEmptyCanvas_DoesNotGrowHistory()
    {
        // 空画布上点清除不该产生一个"无意义的可撤销步骤"
        var c = Build();
        var before = c.UndoDepth;
        c.ClearRegions();
        Assert.Equal(before, c.UndoDepth);
    }

    [Fact]
    public void NewEdit_ClearsRedoStack()
    {
        // 撤销后**做新动作** → 重做链必须被打断（标准编辑器语义）
        var c = Build();
        DrawRect(c, 0.1, 0.1, 0.2, 0.2);
        DrawRect(c, 0.3, 0.3, 0.4, 0.4);
        c.UndoEdit();
        Assert.True(c.RedoDepth > 0);

        DrawRect(c, 0.6, 0.6, 0.7, 0.7);   // 新动作
        Assert.Equal(0, c.RedoDepth);
        Assert.False(c.RedoEdit());
    }

    [Fact]
    public void History_RaisesHistoryChanged()
    {
        // UI 需要据此更新按钮可用性
        var c = Build();
        var raised = 0;
        c.HistoryChanged += (_, _) => raised++;

        DrawRect(c, 0.1, 0.1, 0.2, 0.2);
        Assert.True(raised > 0, "编辑动作后应通知历史变化");

        var after = raised;
        c.UndoEdit();
        Assert.True(raised > after, "撤销后也应通知");
    }

    [Fact]
    public void Undo_DoesNotPolluteUndoStack()
    {
        // 关键不变量：撤销本身**不能**往撤销栈里加东西（否则永远撤不完）
        var c = Build();
        DrawRect(c, 0.1, 0.1, 0.2, 0.2);
        var depth = c.UndoDepth;

        c.UndoEdit();
        Assert.True(c.UndoDepth < depth, "撤销后撤销栈必须变浅");
        Assert.False(c.UndoEdit(), "只做过一次动作 → 撤销一次后应到底");
    }

    [Fact]
    public void History_IsBounded_SoLongSessionsDoNotGrowUnbounded()
    {
        // 上限 100：超出后丢最旧的，内存不无限增长
        var c = Build();
        for (int i = 0; i < 130; i++)
            DrawRect(c, 0.01 * (i % 50), 0.01 * (i % 50), 0.02 + 0.01 * (i % 50), 0.02 + 0.01 * (i % 50));

        Assert.True(c.UndoDepth <= 100, $"撤销栈应有上限，实际 {c.UndoDepth}");
        Assert.True(c.UndoDepth > 0);
    }
}
