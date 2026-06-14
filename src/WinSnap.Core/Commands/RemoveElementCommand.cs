using WinSnap.Core.Annotations;

namespace WinSnap.Core.Commands;

/// <summary>从文档移除一个元素。Do=Remove，Undo=重新 Add（恢复同一引用）。</summary>
public sealed class RemoveElementCommand : IUndoableCommand
{
    private readonly AnnotationDocument _doc;
    private readonly AnnotationElement _element;

    public RemoveElementCommand(AnnotationDocument document, AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(element);
        _doc = document;
        _element = element;
    }

    public void Do() => _doc.Remove(_element);

    public void Undo()
    {
        // 撤销移除：仅当确实不在文档中时才重新加入，避免重复添加抛异常
        if (!_doc.Contains(_element))
            _doc.Add(_element);
    }
}
