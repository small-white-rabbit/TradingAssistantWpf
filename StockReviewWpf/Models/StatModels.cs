using System;
using System.Collections.ObjectModel;

namespace StockReviewWpf.Models;

/// <summary>
/// 统计视图数据模型
/// </summary>
public class OverviewCardItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string ClassName { get; set; } = "";
    public bool IsRanking { get; set; }
    public ObservableCollection<TypeRankingItem>? Ranking { get; set; }
    public bool Clickable { get; set; }
}

public class TypeRankingItem
{
    public string Type { get; set; } = "";
    public string Rate { get; set; } = "";
    public int Total { get; set; }
}

public class EntryTypeStatRow
{
    public string EntryType { get; set; } = "";
    public int Count { get; set; }
    public string WinRate { get; set; } = "";
    public string AvgReturn { get; set; } = "";
    public bool IsParent { get; set; }
    public int Indent { get; set; }
}

public class ProblemStatRow
{
    public string Problem { get; set; } = "";
    public int Count { get; set; }
    public string Percentage { get; set; } = "";
}

public class MonthlyStatRow
{
    public string Month { get; set; } = "";
    public int Total { get; set; }
    public string WinRate { get; set; } = "";
    public string AvgReturn { get; set; } = "";
    public string Best { get; set; } = "";
    public string Worst { get; set; } = "";
}

public class StrongYearRow
{
    public string Year { get; set; } = "";
    public int Count { get; set; }
}

public class StrongMonthRow
{
    public string Month { get; set; } = "";
    public int Count { get; set; }
}

public class ProblemFreqRow
{
    public string Problem { get; set; } = "";
    public int Count { get; set; }
    public string Percentage { get; set; } = "";
}

public class EntryTypeProblemRow
{
    public string EntryType { get; set; } = "";
    public string CommonProblem { get; set; } = "";
    public int Count { get; set; }
}
