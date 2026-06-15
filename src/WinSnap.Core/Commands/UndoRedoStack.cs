namespace WinSnap.Core.Commands;

/// <summary>
/// 撤销/重做栈。<see cref="Execute"/> 立即执行命令并压入 undo 栈、清空 redo 栈；
/// <see cref="Undo"/> / <see cref="Redo"/> 在两栈间搬移并调用对应方法。
/// </summary>
public sealed class UndoRedoStack
{
    private readonly LinkedList<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();

    /// <summary>可选容量上限（&gt;0 时生效）：超出后丢弃最旧的 undo 记录。</summary>
    public int Capacity { get; }

    public UndoRedoStack(int capacity = 0)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    /// <summary>是否有可撤销项。</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>是否有可重做项。</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>undo 栈深度。</summary>
    public int UndoCount => _undo.Count;

    /// <summary>redo 栈深度。</summary>
    public int RedoCount => _redo.Count;

    /// <summary>每次栈状态变化（execute/undo/redo/clear）后触发，便于 UI 刷新按钮可用性。</summary>
    public event Action? StateChanged;

    /// <summary>执行命令：调用 <see cref="IUndoableCommand.Do"/>，压入 undo 栈，并清空 redo 栈。</summary>
    public void Execute(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do();
        _undo.AddLast(command);
        _redo.Clear();
        TrimToCapacity();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 撤销最近一次命令。无可撤销项时返回 false（不抛异常）。
    /// </summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var cmd = _undo.Last!.Value;
        _undo.RemoveLast();
        cmd.Undo();
        _redo.Push(cmd);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 重做最近一次被撤销的命令。无可重做项时返回 false（不抛异常）。
    /// </summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var cmd = _redo.Pop();
        cmd.Do();
        _undo.AddLast(cmd);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>清空两个栈（不调用任何 Do/Undo）。</summary>
    public void Clear()
    {
        bool had = _undo.Count > 0 || _redo.Count > 0;
        _undo.Clear();
        _redo.Clear();
        if (had) StateChanged?.Invoke();
    }

    private void TrimToCapacity()
    {
        if (Capacity <= 0 || _undo.Count <= Capacity) return;
        while (_undo.Count > Capacity)
            _undo.RemoveFirst();
    }
}
