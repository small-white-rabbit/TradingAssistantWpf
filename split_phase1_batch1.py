"""
Phase 1 拆分脚本 - 第 1 批：提取模型类到独立 *Models.cs 文件。
纯物理移动，零字符改动（除加 partial 关键字）。
"""
import re
from pathlib import Path

CORE = Path(r"D:\stock-review-system\TradingAssistantWpf\StockReview.Core")

def read_file(rel):
    p = CORE / rel
    lines = p.read_text(encoding="utf-8-sig").splitlines(keepends=True)
    return lines, p

def extract_header(lines, up_to):
    """提取文件头（using + namespace + 空行），用于新文件"""
    # 找 namespace 行
    ns_line = None
    for i, ln in enumerate(lines):
        if ln.strip().startswith("namespace "):
            ns_line = i
            break
    # using 块在 namespace 之前
    header = []
    for i in range(ns_line):
        header.append(lines[i])
    # namespace 行本身 + 后续空行/注释
    header.append(lines[ns_line])  # namespace ...
    # 找 namespace 后的第一个非空非注释行
    j = ns_line + 1
    while j < len(lines) and j < up_to:
        s = lines[j].strip()
        if s == "" or s.startswith("//"):
            header.append(lines[j])
            j += 1
        else:
            break
    return header, ns_line

# ============ PlanSchedulerService: 1-518 模型，519+ 主类 ============
ps_rel = "Services/PlanSchedulerService.cs"
ps_lines, ps_path = read_file(ps_rel)
# 模型块：行 1 到 518（0-indexed 0-517）
# 主类块：行 519 到末尾（0-indexed 518-）

# 找 namespace
ps_ns_idx = None
for i, ln in enumerate(ps_lines):
    if ln.strip().startswith("namespace "):
        ps_ns_idx = i
        break

# using 行（namespace 之前）
ps_usings = ps_lines[:ps_ns_idx]
# 模型块（从 namespace 行到 518，即 PlanSchedulerService 之前）
# 518 是 PlanSchedulerService 上一行（0-indexed 517 是空行或注释）
# 实际主类声明在 519（1-indexed），即 0-indexed 518
ps_models_body = ps_lines[ps_ns_idx:518]  # namespace 行 到 主类前
ps_main_body = ps_lines[518:]  # 主类到末尾

# 写 PlanSchedulerModels.cs
with open(CORE / "Services" / "PlanSchedulerModels.cs", "w", encoding="utf-8") as f:
    f.writelines(ps_usings)
    f.writelines(ps_models_body)

# 重写 PlanSchedulerService.cs：using + partial 主类
with open(ps_path, "w", encoding="utf-8") as f:
    f.writelines(ps_usings)
    f.writelines(ps_main_body)
    # 确保主类 partial

# 验证：主类首行应改为 public partial class
content = ps_path.read_text(encoding="utf-8")
content = content.replace("public class PlanSchedulerService : IHostedService",
                         "public partial class PlanSchedulerService : IHostedService")
ps_path.write_text(content, encoding="utf-8")
print(f"PlanSchedulerService: 模型提取完成")

# ============ SellPointDetectorService: 1-403 + 3846-3882 模型，404-3845 主类 ============
sp_rel = "Engines/SellPointDetectorService.cs"
sp_lines, sp_path = read_file(sp_rel)

sp_ns_idx = None
for i, ln in enumerate(sp_lines):
    if ln.strip().startswith("namespace "):
        sp_ns_idx = i
        break

sp_usings = sp_lines[:sp_ns_idx]
# 头部模型块：namespace 行 到 403（0-indexed 402），即 SellPointDetectorService 之前
# 主类在 404（1-indexed），0-indexed 403
sp_models_head = sp_lines[sp_ns_idx:403]
# 主类块：404 到 3845（0-indexed 403-3844）
sp_main_body = sp_lines[403:3845]
# 尾部模型块：3846 到末尾（0-indexed 3845-）
sp_models_tail = sp_lines[3845:]

# 写 SellPointModels.cs（合并头尾模型）
with open(CORE / "Engines" / "SellPointModels.cs", "w", encoding="utf-8") as f:
    f.writelines(sp_usings)
    f.writelines(sp_models_head)
    f.writelines(sp_models_tail)

# 重写 SellPointDetectorService.cs：using + partial 主类
with open(sp_path, "w", encoding="utf-8") as f:
    f.writelines(sp_usings)
    f.writelines(sp_main_body)

content = sp_path.read_text(encoding="utf-8")
content = content.replace("public class SellPointDetectorService",
                         "public partial class SellPointDetectorService")
sp_path.write_text(content, encoding="utf-8")
print(f"SellPointDetectorService: 模型提取完成")

# ============ SignalEventService: 1-1679 主类，1680-2033 模型 ============
se_rel = "Services/SignalEventService.cs"
se_lines, se_path = read_file(se_rel)

se_ns_idx = None
for i, ln in enumerate(se_lines):
    if ln.strip().startswith("namespace "):
        se_ns_idx = i
        break

se_usings = se_lines[:se_ns_idx]
# 主类块：namespace 行 到 1679（0-indexed 1678），即 SignalEvent 模型之前
# SignalEvent 在 1680（1-indexed），0-indexed 1679
se_main_body = se_lines[se_ns_idx:1679]
# 模型块：1680 到末尾（0-indexed 1679-）
se_models_body = se_lines[1679:]

# 写 SignalEventModels.cs
with open(CORE / "Services" / "SignalEventModels.cs", "w", encoding="utf-8") as f:
    f.writelines(se_usings)
    f.writelines(se_models_body)

# 重写 SignalEventService.cs
with open(se_path, "w", encoding="utf-8") as f:
    f.writelines(se_usings)
    f.writelines(se_main_body)

content = se_path.read_text(encoding="utf-8")
content = content.replace("public class SignalEventService",
                         "public partial class SignalEventService")
se_path.write_text(content, encoding="utf-8")
print(f"SignalEventService: 模型提取完成")
print("第 1 批完成，请 build + test 验证")
