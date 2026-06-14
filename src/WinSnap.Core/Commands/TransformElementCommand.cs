using WinSnap.Core.Annotations;

namespace WinSnap.Core.Commands;

/// <summary>
/// 变换（移动/缩放/改样式等任意几何或样式修改）命令。
/// 通过 <b>变换前/后两个快照</b>（<see cref="AnnotationElement.Clone"/>）记录状态，
/// Do/Undo 时用 <see cref="AnnotationElement.CopyStateFrom"/> 把对应快照应用回文档中的 live 元素。
/// </summary>
public sealed class TransformElementCommand : IUndoableCommand
{
    private readonly AnnotationElement _target;
    private readonly AnnotationElement _before;
    private readonly AnnotationElement _after;

    /// <summary>
    /// 用显式的前后快照构造。<paramref name="before"/> / <paramref name="after"/> 应是与
    /// <paramref name="target"/> 同类型的快照（通常由 Clone 得到）；构造时会各自再 Clone 一份以隔离外部改动。
    /// </summary>
    public TransformElementCommand(
        AnnotationElement target,
        AnnotationElement before,
        AnnotationElement after)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.GetType() != target.GetType() || after.GetType() != target.GetType())
            throw new ArgumentException("before/after 快照必须与 target 同类型。");
        _target = target;
        _before = before.Clone();
        _after = after.Clone();
    }

    /// <summary>
    /// 便捷工厂：在调用 <paramref name="mutate"/> 修改 <paramref name="target"/> 前后各拍一张快照，
    /// 返回对应的变换命令（命令构造时 target 已处于"变换后"状态，Do 是幂等地再次应用 after）。
    /// </summary>
    public static TransformElementCommand Capture(AnnotationElement target, Action<AnnotationElement> mutate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mutate);
        var before = target.Clone();
        mutate(target);
        var after = target.Clone();
        return new TransformElementCommand(target, before, after);
    }

    public void Do() => _target.CopyStateFrom(_after);

    public void Undo() => _target.CopyStateFrom(_before);
}
