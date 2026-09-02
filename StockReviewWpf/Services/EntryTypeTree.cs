using System;
using System.Collections.Generic;
using System.Linq;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.Services;

/// <summary>
/// 进场类型父子树构建器（通用组件，对齐原版表单的 Ne 计算属性）：
///   entryTypes 表中 parentId 指向存在的父级 → 挂到父级 children；
///   否则（无父级 / 父级不在列表内）→ 顶层根节点。
/// 渲染约定：根节点名作分组标题；children 非空用 children 作选项，为空则根自身作唯一选项。
/// 使用方：交易录入表单（TradeFormView）、添加计划弹窗（AddPlanDialogViewModel）等，
/// 多窗口引用同一构建逻辑，保证与「设置-进场类型管理」的数据形态一致。
/// </summary>
public static class EntryTypeTree
{
    /// <summary>
    /// 由扁平的活跃进场类型列表构建父子树。
    /// 输入需已按 isActive 过滤；输出按 sortOrder 排序，无子类的根节点已注入自身作唯一子项。
    /// </summary>
    public static List<EntryTypeItem> Build(IEnumerable<EntryTypeItem> items)
    {
        var nodes = items.Where(n => n.Id > 0).ToList();

        var byId = new Dictionary<int, EntryTypeItem>();
        foreach (var n in nodes)
            if (!byId.ContainsKey(n.Id)) byId[n.Id] = n;

        // 幂等关键：清空每个节点上残留的 Children，避免对同一对象实例多次调用 Build 时
        // 在已有集合上累积追加导致子类型重复（交易录入弹窗每次打开都会重建一次树）。
        foreach (var n in nodes)
            n.Children.Clear();

        var roots = new List<EntryTypeItem>();
        foreach (var n in nodes)
        {
            if (n.ParentId.HasValue && byId.TryGetValue(n.ParentId.Value, out var parent))
                parent.Children.Add(n);
            else
                roots.Add(n);
        }

        roots.Sort((a, b) => a.SortOrder - b.SortOrder);
        foreach (var root in roots)
        {
            root.Children = new System.Collections.ObjectModel.ObservableCollection<EntryTypeItem>(
                root.Children.OrderBy(c => c.SortOrder));
            // 无子类的父类：自身作为组内唯一选项（对齐原版 children 为空的回退）
            if (root.Children.Count == 0)
                root.Children.Add(new EntryTypeItem { Id = root.Id, Name = root.Name });
        }
        return roots;
    }
}
