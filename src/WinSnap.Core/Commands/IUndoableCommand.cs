namespace WinSnap.Core.Commands;

/// <summary>可撤销/重做的命令。<see cref="Do"/> 与 <see cref="Undo"/> 必须互为逆操作。</summary>
public interface IUndoableCommand
{
    /// <summary>执行（或重做）该操作。</summary>
    void Do();

    /// <summary>撤销该操作，恢复到 <see cref="Do"/> 之前的状态。</summary>
    void Undo();
}
