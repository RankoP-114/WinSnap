using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>
/// 标注文档：持有一组 <see cref="AnnotationElement"/>，提供增删、按 ZIndex 排序枚举与命中测试。
/// 不直接负责撤销/重做（由 <c>UndoRedoStack</c> + 具体命令驱动），但命令会调用其 Add/Remove。
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<AnnotationElement> _elements = new();

    /// <summary>当前元素只读视图（按插入顺序，未排序）。</summary>
    public IReadOnlyList<AnnotationElement> Elements => _elements;

    /// <summary>元素数量。</summary>
    public int Count => _elements.Count;

    /// <summary>添加元素到末尾。重复添加同一引用将抛出。</summary>
    public void Add(AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_elements.Contains(element))
            throw new InvalidOperationException("该元素已在文档中。");
        if (FindById(element.Id) is not null)
            throw new InvalidOperationException("该元素 Id 已在文档中。");
        _elements.Add(element);
    }

    /// <summary>把元素插回指定插入顺序位置。越界索引会钳制到有效范围。</summary>
    public void Insert(int index, AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_elements.Contains(element))
            throw new InvalidOperationException("该元素已在文档中。");
        if (FindById(element.Id) is not null)
            throw new InvalidOperationException("该元素 Id 已在文档中。");

        int clamped = Math.Clamp(index, 0, _elements.Count);
        _elements.Insert(clamped, element);
    }

    /// <summary>移除元素。成功返回 true。</summary>
    public bool Remove(AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _elements.Remove(element);
    }

    /// <summary>返回元素当前插入顺序索引；不存在返回 -1。</summary>
    public int IndexOf(AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _elements.IndexOf(element);
    }

    /// <summary>按 Id 查找。</summary>
    public AnnotationElement? FindById(Guid id)
    {
        foreach (var e in _elements)
            if (e.Id == id) return e;
        return null;
    }

    /// <summary>是否包含某元素引用。</summary>
    public bool Contains(AnnotationElement element) => _elements.Contains(element);

    /// <summary>清空。</summary>
    public void Clear() => _elements.Clear();

    /// <summary>
    /// 按 ZIndex 升序（同 ZIndex 保持插入先后）枚举 —— 即从最底层到最顶层的绘制顺序。
    /// </summary>
    public IEnumerable<AnnotationElement> InZOrder()
        => _elements
            .Select((e, i) => (e, i))
            .OrderBy(t => t.e.ZIndex)
            .ThenBy(t => t.i)
            .Select(t => t.e);

    /// <summary>
    /// 命中测试：返回点 <paramref name="p"/> 命中的"最上层"元素（ZIndex 最大者优先；
    /// 同 ZIndex 取后添加者），未命中返回 null。
    /// </summary>
    public AnnotationElement? HitTest(PointInt p, double tolerance = 0.0)
    {
        AnnotationElement? best = null;
        int bestZ = int.MinValue;
        int bestIndex = -1;
        for (int i = 0; i < _elements.Count; i++)
        {
            var e = _elements[i];
            if (!e.HitTest(p, tolerance)) continue;
            // 选 ZIndex 更大者；ZIndex 相同选后添加（索引更大）者
            if (best is null || e.ZIndex > bestZ || (e.ZIndex == bestZ && i > bestIndex))
            {
                best = e;
                bestZ = e.ZIndex;
                bestIndex = i;
            }
        }
        return best;
    }

    /// <summary>把指定元素移到最顶层（ZIndex 设为当前最大值+1）。</summary>
    public void BringToFront(AnnotationElement element)
    {
        if (!_elements.Contains(element)) return;
        int maxZ = int.MinValue;
        foreach (var e in _elements)
            if (e.ZIndex > maxZ) maxZ = e.ZIndex;
        element.ZIndex = (maxZ == int.MinValue) ? 0 : maxZ + 1;
    }
}
