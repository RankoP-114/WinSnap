using WinSnap.Core.Annotations;

namespace WinSnap.Core.Commands;

/// <summary>向文档添加一个元素。Do=Add，Undo=Remove。</summary>
public sealed class AddElementCommand : IUndoableCommand
{
    private readonly AnnotationDocument _doc;
    private readonly AnnotationElement _element;

    public AddElementCommand(AnnotationDocument document, AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(element);
        _doc = document;
        _element = element;
    }

    public void Do() => _doc.Add(_element);

    public void Undo() => _doc.Remove(_element);
}
