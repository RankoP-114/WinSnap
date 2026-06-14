namespace WinSnap.App.Annotation;

/// <summary>覆盖层当前激活的标注工具。None 表示处于选区编辑模式。</summary>
public enum AnnotationTool
{
    None,
    Rectangle,
    Ellipse,
    Arrow,
    Freehand,
    Text,
    Mosaic,
    Number,
}
