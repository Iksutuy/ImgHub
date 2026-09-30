using ImgHub.Core.Models;

namespace ImgHub.Core.Storage;

/// <summary>
/// <see cref="Session.Items"/> 的存储容器：**内部加锁**的 Item 列表（P0-4）。
///
/// 为什么需要它：`Items` 会被**跨线程**访问 ——
///   · <c>MainViewModel.ResumePendingAsync</c> 全程 <c>ConfigureAwait(false)</c>，
///     在线程池线程调 <see cref="Session.Push"/>；
///   · 同时 UI 线程可能在遍历同一个列表刷新历史。
/// 裸 <c>List&lt;Item&gt;</c> 在这种情形下枚举会抛
/// <c>InvalidOperationException: Collection was modified</c>，落盘时还可能丢行。
///
/// 设计要点：
///   · **枚举返回快照**（<see cref="GetEnumerator"/> 内部先复制）→ 遍历期间不受并发修改影响，
///     也不会在 <c>foreach</c> 中抛异常；
///   · 刻意**不实现 <c>IList&lt;Item&gt;</c>**：LINQ（如 <c>Take</c>）对 <c>IList</c> 有
///     「按下标直读」的快速路径，会绕过快照、在并发删除时越界。
///     需要「按快照取子集」时请显式用 <see cref="Snapshot"/>；
///   · 变更方法都持同一把 <c>_gate</c>，读（Count / 索引器）也持锁，保证不读到半更新状态。
/// </summary>
public sealed class ItemStore : IReadOnlyList<Item>
{
    private readonly List<Item> _list = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _list.Count; }
    }

    /// <summary><c>Items[0]</c> = 当前（最新）一张。</summary>
    public Item this[int index]
    {
        get { lock (_gate) return _list[index]; }
    }

    public void Add(Item item) { lock (_gate) _list.Add(item); }

    public void Insert(int index, Item item) { lock (_gate) _list.Insert(index, item); }

    public void Clear() { lock (_gate) _list.Clear(); }

    public bool Remove(Item item) { lock (_gate) return _list.Remove(item); }

    public void RemoveAt(int index) { lock (_gate) _list.RemoveAt(index); }

    /// <summary>
    /// 原子裁剪：保留前 <paramref name="max"/> 项，其余丢弃。
    /// ⚠️ 不要用「读 <see cref="Count"/> → 再 <see cref="RemoveAt"/>」的组合 ——
    /// 两步之间没有锁，并发写入时会越界（TOCTOU）。这个方法是那条路径的替代品。
    /// </summary>
    public void TrimTail(int max)
    {
        lock (_gate)
        {
            if (max < 0) max = 0;
            if (_list.Count > max) _list.RemoveRange(max, _list.Count - max);
        }
    }

    /// <summary>原子地尝试移除下标 <paramref name="index"/> 的项；越界返回 false（不抛）。</summary>
    public bool TryRemoveAt(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _list.Count) return false;
            _list.RemoveAt(index);
            return true;
        }
    }

    /// <summary>原子地尝试移除并返回首项（「撤回」用：拿到被移除的那张）。</summary>
    public Item? TryTakeFirst()
    {
        lock (_gate)
        {
            if (_list.Count == 0) return null;
            var first = _list[0];
            _list.RemoveAt(0);
            return first;
        }
    }

    public int IndexOf(Item item) { lock (_gate) return _list.IndexOf(item); }

    public bool Contains(Item item) { lock (_gate) return _list.Contains(item); }

    /// <summary>
    /// 取一份**独立快照**。需要「遍历 + 过滤/裁剪/持久化」时一律用它，
    /// 避免在枚举期间被并发修改影响（也避免 LINQ 走 IList 快速路径）。
    /// </summary>
    public List<Item> Snapshot()
    {
        lock (_gate) return new List<Item>(_list);
    }

    /// <summary>枚举走快照 → <c>foreach</c> 不会因并发插入/删除而抛。</summary>
    public IEnumerator<Item> GetEnumerator() => Snapshot().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
