using System;
using System.Collections.ObjectModel;

namespace StockReviewWpf.Models;

/// <summary>
/// 洞察/心得记录数据模型
/// </summary>
public class InsightRecord
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public int Importance { get; set; }
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Tags { get; set; } = "";
}

/// <summary>
/// 案例数据模型
/// </summary>
public class CaseItem
{
    public int Id { get; set; }
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string TradeDate { get; set; } = "";
    public string TotalReturn { get; set; } = "";
    public string CaseType { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string EntryPrice { get; set; } = "";
    public string ExitPrice { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>
/// 强势股数据模型
/// </summary>
public class StrongStockItem
{
    public int Id { get; set; }
    public string Date { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string Change { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>
/// 进场类型数据模型
/// </summary>
public class EntryTypeItem
{
    public int Id { get; set; }
    public int SortOrder { get; set; }
    public string TypeName { get; set; } = "";
    public string Name { get => TypeName; set => TypeName = value; }
    public string Description { get; set; } = "";
    public string Color { get; set; } = "#409EFF";
    public bool IsStrongType { get; set; }
    public bool IsActive { get; set; } = true;
    public int? ParentId { get; set; }
    public ObservableCollection<EntryTypeItem> Children { get; set; } = new();
    public bool HasChildren => Children.Count > 0;
    /// <summary>树形平铺展示：子级缩进（对齐原版 el-table 树形缩进）</summary>
    public string DisplayName => ParentId.HasValue ? "      └ " + TypeName : TypeName;
}

/// <summary>
/// 问题标签数据模型
/// </summary>
public class ProblemTagItem
{
    public int Id { get; set; }
    public int SortOrder { get; set; }
    public string TagName { get; set; } = "";
    public string Name { get => TagName; set => TagName = value; }
    public string Description { get; set; } = "";
    public string Color { get; set; } = "#F56C6C";
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// 年月视图交易记录
/// </summary>
public class TradeRecord
{
    public int Id { get; set; }
    public string TradeDate { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string EntryPrice { get; set; } = "";
    public string TotalReturn { get; set; } = "";
    public string PositionStatus { get; set; } = "";
    public string ProblemTags { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>
/// 月份数据分组
/// </summary>
public class MonthDataGroup
{
    public string Key { get; set; } = "";
    public int Year { get; set; }
    public int Month { get; set; }
    public ObservableCollection<TradeRecord> Trades { get; set; } = new();
    public ObservableCollection<StrongStockItem> StrongStocks { get; set; } = new();
    public MonthStats Stats { get; set; } = new();
}

public class MonthStats
{
    public int Total { get; set; }
    public string WinRate { get; set; } = "0";
    public string AvgReturn { get; set; } = "0";
}

/// <summary>
/// 日记本记录
/// </summary>
public class DiaryRecord
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Mood { get; set; } = "";
}
