using WinSnap.Core.Annotations;
using WinSnap.Core.Commands;
using WinSnap.Core.Primitives;

namespace WinSnap.Core.Tests;

public class UndoRedoTests
{
    /// <summary>记录 Do/Undo 调用顺序的探针命令。</summary>
    private sealed class ProbeCommand : IUndoableCommand
    {
        private readonly List<string> _log;
        private readonly string _name;
        public ProbeCommand(List<string> log, string name) { _log = log; _name = name; }
        public void Do() => _log.Add($"do:{_name}");
        public void Undo() => _log.Add($"undo:{_name}");
    }

    [Fact]
    public void Execute_RunsDo_And_SetsCanUndo()
    {
        var log = new List<string>();
        var stack = new UndoRedoStack();
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);

        stack.Execute(new ProbeCommand(log, "A"));
        Assert.Equal(new[] { "do:A" }, log);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void UndoRedo_Order_And_State()
    {
        var log = new List<string>();
        var stack = new UndoRedoStack();
        stack.Execute(new ProbeCommand(log, "A"));
        stack.Execute(new ProbeCommand(log, "B"));
        log.Clear();

        // Undo 应逆序：先 B 后 A
        Assert.True(stack.Undo());
        Assert.True(stack.Undo());
        Assert.False(stack.Undo()); // 空了
        Assert.Equal(new[] { "undo:B", "undo:A" }, log);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);

        log.Clear();
        // Redo 顺序：A 再 B
        Assert.True(stack.Redo());
        Assert.True(stack.Redo());
        Assert.False(stack.Redo());
        Assert.Equal(new[] { "do:A", "do:B" }, log);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Execute_ClearsRedoStack()
    {
        var log = new List<string>();
        var stack = new UndoRedoStack();
        stack.Execute(new ProbeCommand(log, "A"));
        stack.Undo();
        Assert.True(stack.CanRedo);

        // 新 Execute 后 redo 栈必须清空
        stack.Execute(new ProbeCommand(log, "B"));
        Assert.False(stack.CanRedo);
        Assert.Equal(0, stack.RedoCount);
        Assert.True(stack.CanUndo);
    }

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var stack = new UndoRedoStack();
        stack.Execute(new ProbeCommand(new List<string>(), "A"));
        stack.Undo();
        stack.Clear();
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal(0, stack.UndoCount);
        Assert.Equal(0, stack.RedoCount);
    }

    [Fact]
    public void StateChanged_FiresOnExecuteUndoRedo()
    {
        var stack = new UndoRedoStack();
        int fired = 0;
        stack.StateChanged += () => fired++;
        stack.Execute(new ProbeCommand(new List<string>(), "A"));
        stack.Undo();
        stack.Redo();
        Assert.Equal(3, fired);
    }

    [Fact]
    public void Capacity_TrimsOldest_KeepsRecentUndoable()
    {
        var log = new List<string>();
        var stack = new UndoRedoStack(capacity: 2);
        stack.Execute(new ProbeCommand(log, "A"));
        stack.Execute(new ProbeCommand(log, "B"));
        stack.Execute(new ProbeCommand(log, "C")); // A 应被丢弃
        Assert.Equal(2, stack.UndoCount);

        log.Clear();
        // 还能 undo 两次（C、B），第三次失败（A 已丢）
        Assert.True(stack.Undo());
        Assert.True(stack.Undo());
        Assert.False(stack.Undo());
        Assert.Equal(new[] { "undo:C", "undo:B" }, log);
    }

    // ---- 具体命令与文档协作 ----

    [Fact]
    public void AddElementCommand_DoAdds_UndoRemoves()
    {
        var doc = new AnnotationDocument();
        var el = new RectangleAnnotation { Rect = new RectInt(0, 0, 10, 10) };
        var stack = new UndoRedoStack();

        stack.Execute(new AddElementCommand(doc, el));
        Assert.Equal(1, doc.Count);
        Assert.True(doc.Contains(el));

        stack.Undo();
        Assert.Equal(0, doc.Count);

        stack.Redo();
        Assert.Equal(1, doc.Count);
        Assert.True(doc.Contains(el));
    }

    [Fact]
    public void RemoveElementCommand_DoRemoves_UndoRestores()
    {
        var doc = new AnnotationDocument();
        var el = new EllipseAnnotation { Rect = new RectInt(0, 0, 10, 10) };
        doc.Add(el);
        var stack = new UndoRedoStack();

        stack.Execute(new RemoveElementCommand(doc, el));
        Assert.Equal(0, doc.Count);

        stack.Undo();
        Assert.Equal(1, doc.Count);
        Assert.Same(el, doc.Elements[0]);
    }

    [Fact]
    public void RemoveElementCommand_UndoRestoresOriginalInsertionIndex()
    {
        var doc = new AnnotationDocument();
        var first = new RectangleAnnotation { Rect = new RectInt(0, 0, 10, 10) };
        var removed = new EllipseAnnotation { Rect = new RectInt(10, 0, 10, 10) };
        var last = new ArrowAnnotation { Start = new PointInt(0, 0), End = new PointInt(10, 10) };
        doc.Add(first);
        doc.Add(removed);
        doc.Add(last);

        var stack = new UndoRedoStack();
        stack.Execute(new RemoveElementCommand(doc, removed));
        stack.Undo();

        Assert.Equal(new AnnotationElement[] { first, removed, last }, doc.Elements);
    }

    [Fact]
    public void TransformElementCommand_Capture_UndoRedo_RestoresGeometry()
    {
        var doc = new AnnotationDocument();
        var el = new RectangleAnnotation { Rect = new RectInt(0, 0, 10, 10) };
        doc.Add(el);
        var stack = new UndoRedoStack();

        // 用 Capture 包裹一次平移+放大
        var cmd = TransformElementCommand.Capture(el, e =>
        {
            var rect = (RectangleAnnotation)e;
            rect.Rect = new RectInt(20, 20, 40, 40);
        });
        // Capture 后 el 已是变换后的状态
        Assert.Equal(new RectInt(20, 20, 40, 40), el.Rect);

        stack.Execute(cmd); // Do 再次应用 after（幂等）
        Assert.Equal(new RectInt(20, 20, 40, 40), el.Rect);

        stack.Undo();
        Assert.Equal(new RectInt(0, 0, 10, 10), el.Rect); // 回到变换前
        // 文档持有的仍是同一引用，几何已被复原
        Assert.Same(el, doc.Elements[0]);

        stack.Redo();
        Assert.Equal(new RectInt(20, 20, 40, 40), el.Rect);
    }

    [Fact]
    public void TransformElementCommand_ExplicitBeforeAfter()
    {
        var el = new ArrowAnnotation { Start = new PointInt(0, 0), End = new PointInt(5, 5) };
        var before = (ArrowAnnotation)el.Clone();
        el.End = new PointInt(50, 50);
        var after = (ArrowAnnotation)el.Clone();

        var cmd = new TransformElementCommand(el, before, after);
        cmd.Undo();
        Assert.Equal(new PointInt(5, 5), el.End);
        cmd.Do();
        Assert.Equal(new PointInt(50, 50), el.End);
    }
}
