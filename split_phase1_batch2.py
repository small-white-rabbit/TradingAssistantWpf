"""
Phase 1 拆分脚本 - 第 2 批：主类拆分为多个 partial 文件。
按方案定义的行号边界精确切片，整块移动，零字符改动。
"""
from pathlib import Path

CORE = Path(r"D:\stock-review-system\TradingAssistantWpf\StockReview.Core")

def read_lines(rel):
    return (CORE / rel).read_text(encoding="utf-8-sig").splitlines(keepends=True)

def write_file(rel, lines):
    p = CORE / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text("".join(lines), encoding="utf-8")

# ============ PlanSchedulerService 拆分（当前 4121 行主类）============
ps = read_lines("Services/PlanSchedulerService.cs")

# 骨架：1-797（using + namespace + class + 字段 + 构造/生命周期/时段调度）
# 这部分保留在主文件
ps_skeleton = ps[:797]  # 行 1-797（0-indexed 0-796）

# 1. Checking.cs: 797-1878（信号检测路由）
ps_checking = ps[797:1878]

# 2. Snapshots.cs: 1878-2338（节流/波门/快照采集/缓存）
ps_snapshots = ps[1878:2338]

# 3. Reminders.cs: 2338-2911（自定义提醒/闲时洞察/盘中摘要/周末总结/回填评估）
ps_reminders = ps[2338:2911]

# 4. Evolution.cs: 2911-3752（自进化引擎/参数优化/加载保存）
ps_evolution = ps[2911:3752]

# 5. Futu.cs: 3752-4121（富途订阅/推送驱动/盘后通知状态）
ps_futu = ps[3752:]

# partial 头部模板
def partial_header(usings, namespace, classname):
    return usings + [f"\nnamespace {namespace};\n\n", f"public partial class {classname}\n", "{\n"]

# 从原文件提取 using 块
def extract_usings(lines):
    usings = []
    for ln in lines:
        if ln.strip().startswith("using ") or ln.strip() == "":
            usings.append(ln)
        elif ln.strip().startswith("namespace "):
            break
    # 去掉尾部空行
    while usings and usings[-1].strip() == "":
        usings.pop()
    return usings

ps_usings = extract_usings(ps)

# 写 5 个 partial 文件（每个 = using + namespace + partial class + 方法块 + 闭合括号）
for name, body in [
    ("PlanSchedulerService.Checking.cs", ps_checking),
    ("PlanSchedulerService.Snapshots.cs", ps_snapshots),
    ("PlanSchedulerService.Reminders.cs", ps_reminders),
    ("PlanSchedulerService.Evolution.cs", ps_evolution),
    ("PlanSchedulerService.Futu.cs", ps_futu),
]:
    # 去掉 body 尾部多余的 }
    content = "".join(body)
    # 找最后一个 } 的位置（类闭合括号），方法块不应包含它
    # 实际上 body 是中间切片，不应有类闭合括号
    write_file(f"Services/{name}", ps_usings + [
        f"\nnamespace StockReview.Core.Services;\n\n",
        f"public partial class PlanSchedulerService\n",
        "{\n",
    ] + body + ["}\n"])

# 重写主文件（骨架）
write_file("Services/PlanSchedulerService.cs", ps_skeleton + ["}\n"])
print("PlanSchedulerService: 主类拆分完成（5 个 partial）")

# ============ SellPointDetectorService 拆分 ============
sp = read_lines("Engines/SellPointDetectorService.cs")
sp_usings = extract_usings(sp)

# 骨架：1-433（using + namespace + class + 字段 + 构造 + 配置 + 乘数 + 状态 + 归一化）
sp_skeleton = sp[:433]

# 1. Analyze.cs: 433-2865（Analyze 主流程 + CreateBreakSignal）
sp_analyze = sp[433:2865]

# 2. Indicators.cs: 2865-3338（ATR/RSI/WR/MFI/超买共振/市场上下文/形态几何）
sp_indicators = sp[2865:3338]

# 3. Scoring.cs: 3338-（权重/去重/密度/EvaluateSignals/动量确认/腿部量能）
sp_scoring = sp[3338:]

for name, body in [
    ("SellPointDetectorService.Analyze.cs", sp_analyze),
    ("SellPointDetectorService.Indicators.cs", sp_indicators),
    ("SellPointDetectorService.Scoring.cs", sp_scoring),
]:
    write_file(f"Engines/{name}", sp_usings + [
        f"\nnamespace StockReview.Core.Engines;\n\n",
        f"public partial class SellPointDetectorService\n",
        "{\n",
    ] + body + ["}\n"])

# 重写主文件（骨架）
write_file("Engines/SellPointDetectorService.cs", sp_skeleton + ["}\n"])
print("SellPointDetectorService: 主类拆分完成（3 个 partial）")

# ============ SignalEventService 拆分 ============
se = read_lines("Services/SignalEventService.cs")
se_usings = extract_usings(se)

# 骨架：1-596（using + namespace + class + 构造 + 存储 + 事件写入/查询）
se_skeleton = se[:596]

# 1. Evaluation.cs: 596-980（评估窗口计算/回放/统计基础）
se_evaluation = se[596:980]

# 2. Stats.cs: 980-（回放参数/因子奖励/归因/复盘建议/清理）
se_stats = se[980:]

for name, body in [
    ("SignalEventService.Evaluation.cs", se_evaluation),
    ("SignalEventService.Stats.cs", se_stats),
]:
    write_file(f"Services/{name}", se_usings + [
        f"\nnamespace StockReview.Core.Services;\n\n",
        f"public partial class SignalEventService\n",
        "{\n",
    ] + body + ["}\n"])

# 重写主文件（骨架）
write_file("Services/SignalEventService.cs", se_skeleton + ["}\n"])
print("SignalEventService: 主类拆分完成（2 个 partial）")
print("第 2 批完成，请 build + test 验证")
