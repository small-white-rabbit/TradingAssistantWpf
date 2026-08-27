using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 标注存储服务 - 对应 Electron 版 annotationStore.js
/// 管理用户在分时回放图上手动标注的买卖点
/// </summary>
public class AnnotationService
{
    private readonly DatabaseService _db;
    private const string StorageKey = "scalping_annotations";

    private List<Annotation> _annotations = new();
    private bool _loaded;

    // ============ 理由选项 ============

    public static readonly List<(string Value, string Label)> BuyReasons = new()
    {
        ("vwap_dip", "均价线回踩"),
        ("w_bottom", "分时W底"),
        ("panic_buy", "急跌缩量"),
        ("tail_buy", "尾盘回补"),
        ("reversal_kline", "反转K线")
    };

    public static readonly List<(string Value, string Label)> SellReasons = new()
    {
        ("surge_pullback", "冲高回落"),
        ("volume_stagnant", "放量滞涨"),
        ("ma_suppress", "分时均线压制"),
        ("double_top", "双顶形态"),
        ("fishing_line", "钓鱼线"),
        ("triple_top", "三次上攻不破"),
        ("platform_breakdown", "跌破平台"),
        ("high_deviation_pullback", "高乖离回落"),
        ("vwap_breakdown", "跌破均价线"),
        ("vwap_rejection", "均线挡道"),
        ("vwap_slope_down", "均价线拐头"),
        ("late_session_exit", "尾盘出逃"),
        ("weak_rebound_failure", "缩量反弹失败")
    };

    public AnnotationService(DatabaseService db)
    {
        _db = db;
    }

    // ============ 持久化 ============

    private void Load()
    {
        if (_loaded) return;
        try
        {
            var row = _db.GetById("appConfig", StorageKey);
            if (row != null && row.TryGetValue("value", out var val) && val != null)
            {
                var json = val.ToString();
                _annotations = JsonSerializer.Deserialize<List<Annotation>>(json!) ?? new();
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Annotation] 加载标注数据失败: {Msg}", e.Message);
        }
        _loaded = true;
    }

    private void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_annotations);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = StorageKey,
                ["value"] = json
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Annotation] 保存标注数据失败: {Msg}", e.Message);
        }
    }

    // ============ CRUD ============

    /// <summary>
    /// 添加标注
    /// </summary>
    public string AddAnnotation(Annotation ann)
    {
        Load();
        var record = new Annotation
        {
            Id = $"ann_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N").Substring(0, 8)}",
            StockCode = ann.StockCode,
            Date = ann.Date,
            BarIndex = ann.BarIndex,
            Time = ann.Time ?? "",
            Price = ann.Price,
            Side = ann.Side ?? "buy",
            Reason = ann.Reason ?? "custom",
            ReasonLabel = ann.ReasonLabel ?? ann.Reason ?? "",
            Features = ann.Features ?? new(),
            Note = ann.Note ?? "",
            CreatedAt = DateTime.UtcNow.ToString("o")
        };
        _annotations.Add(record);
        Persist();
        return record.Id;
    }

    /// <summary>
    /// 删除标注
    /// </summary>
    public void RemoveAnnotation(string id)
    {
        var idx = _annotations.FindIndex(a => a.Id == id);
        if (idx >= 0)
        {
            _annotations.RemoveAt(idx);
            Persist();
        }
    }

    /// <summary>
    /// 获取全部标注
    /// </summary>
    public List<Annotation> GetAll()
    {
        Load();
        return _annotations;
    }

    /// <summary>
    /// 按理由筛选
    /// </summary>
    public List<Annotation> GetByReason(string reason)
    {
        Load();
        return _annotations.Where(a => a.Reason == reason).ToList();
    }

    /// <summary>
    /// 按股票筛选
    /// </summary>
    public List<Annotation> GetByStock(string stockCode)
    {
        Load();
        return _annotations.Where(a => a.StockCode == stockCode).ToList();
    }

    /// <summary>
    /// 按股票+日期筛选（用于图表上渲染标注）
    /// </summary>
    public List<Annotation> GetByStockDate(string stockCode, string date)
    {
        Load();
        return _annotations.Where(a => a.StockCode == stockCode && a.Date == date).ToList();
    }

    /// <summary>
    /// 清空全部
    /// </summary>
    public void ClearAll()
    {
        _annotations.Clear();
        Persist();
    }

    /// <summary>
    /// 导出 JSON
    /// </summary>
    public string ExportJson()
    {
        Load();
        return JsonSerializer.Serialize(_annotations, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 导入 JSON
    /// </summary>
    public int ImportJson(string jsonStr)
    {
        try
        {
            var data = JsonSerializer.Deserialize<List<Annotation>>(jsonStr);
            if (data != null)
            {
                _annotations = data;
                Persist();
                return data.Count;
            }
            return 0;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Annotation] 导入失败: {Msg}", e.Message);
            return 0;
        }
    }

    // ============ 统计 ============

    public int TotalCount
    {
        get { Load(); return _annotations.Count; }
    }

    public int BuyCount
    {
        get { Load(); return _annotations.Count(a => a.Side == "buy"); }
    }

    public int SellCount
    {
        get { Load(); return _annotations.Count(a => a.Side == "sell"); }
    }
}

// ============ 数据模型 ============

public class Annotation
{
    public string Id { get; set; } = "";
    public string? StockCode { get; set; }
    public string? Date { get; set; }
    public int BarIndex { get; set; }
    public string Time { get; set; } = "";
    public decimal Price { get; set; }
    public string Side { get; set; } = "buy";
    public string Reason { get; set; } = "custom";
    public string ReasonLabel { get; set; } = "";
    public Dictionary<string, object?> Features { get; set; } = new();
    public string Note { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
