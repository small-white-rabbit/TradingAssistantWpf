// 跨语言对照基准 —— 抽取自原版基准 detectMultiWindowRapid
// 与 C# StockReview.Core/Services/PlanSchedulerService.cs:1830 DetectMultiWindowRapid 逐行对应。
//
// 关键说明：JS 原版与 C# 现版的 rapidWindows *默认配置* 不一致（见下 JS_RAPID vs C# MonitorConfig），
// 本基线统一使用 JS 原版默认窗口，以隔离验证「算法逻辑（方向判定 / 最长窗口优先 / ratio>2 短窗口优先）」
// 是否翻译正确，不受默认配置差异干扰。
//
// 注意：C# 版在 bestMatch 里额外计算了 WindowMinutes（recent 时间戳差），JS 版不返回该字段，
// 故跨语言比对只比对 direction / changePct / windowBars / windowLabel / cooldownMs。

function detectMultiWindowRapid(snapshots, windows) {
  if (!snapshots || snapshots.length < 9) return null;

  const win = windows;
  let bestMatch = null;

  for (const w of win) {
    if (snapshots.length < w.bars) continue;

    const recent = snapshots.slice(-w.bars);
    const prices = recent.map(s => Number(s.price)).filter(v => Number.isFinite(v) && v > 0);
    if (prices.length < 2) continue;

    const firstPrice = prices[0];
    const lastPrice = prices[prices.length - 1];
    const wLow = Math.min(...prices);
    const wHigh = Math.max(...prices);
    const changePct = (lastPrice - firstPrice) / firstPrice * 100;
    const volatilityPct = (wHigh - wLow) / Math.min(wLow, firstPrice) * 100;

    let dir = changePct >= w.pct ? 'up' : changePct <= -w.pct ? 'down' : 'normal';
    if (dir === 'normal' && volatilityPct >= w.pct) {
      dir = lastPrice < firstPrice ? 'down' : lastPrice > firstPrice ? 'up' : 'normal';
    }

    if (dir !== 'normal') {
      const ratio = Math.abs(changePct) / w.pct;
      if (!bestMatch || w.bars > bestMatch.windowBars || ratio > 2) {
        bestMatch = {
          direction: dir,
          windowLabel: w.label,
          changePct,
          windowBars: w.bars,
          cooldownMs: w.cooldownMs
        };
      }
    }
  }

  return bestMatch;
}

// 原版默认窗口，按分钟设计
const JS_RAPID = [
  { bars: 9,   pct: 1.0, label: '脉冲',     cooldownMs: 2 * 60 * 1000 },
  { bars: 30,  pct: 2.0, label: '中速',     cooldownMs: 3 * 60 * 1000 },
  { bars: 60,  pct: 3.0, label: '慢牛',     cooldownMs: 5 * 60 * 1000 },
  { bars: 120, pct: 4.0, label: '持续推升', cooldownMs: 10 * 60 * 1000 },
];

const mk = (prices) => prices.map(p => ({ price: p }));

// S1 快涨命中「中速」窗口(30 bars, pct 2%)：35 快照，末尾 5 根 100→103（+3%）
const s1 = mk([...Array(30).fill(100), 100.5, 101, 101.5, 102, 102.5, 103]);

// S2 快跌命中「脉冲」窗口(9 bars, pct 1%)且 ratio>2 选最短：12 快照，末 9 根 100→90（-10%）
const s2 = mk([100, 100, 100, 100, 99, 98, 97, 96, 95, 94, 93, 90]);

// S3 不触发：20 快照小幅波动（±0.2）
const s3 = mk([100, 100.1, 99.9, 100.05, 99.95, 100.1, 99.92, 100.03, 99.97, 100.08,
               99.94, 100.02, 99.98, 100.06, 99.93, 100.01, 99.99, 100.04, 99.96, 100]);

// S4 方向兜底（波动率）：12 快照，末 9 根首尾 100→100.3（+0.3% 不足 pct1），但中间剧烈波动(80~120)
const s4 = mk([100, 100, 100, 100, 120, 80, 100.3, 100.3, 100.3, 100.3, 100.3, 100.3]);

const result = {
  S1_up_mid: detectMultiWindowRapid(s1, JS_RAPID),
  S2_down_pulse: detectMultiWindowRapid(s2, JS_RAPID),
  S3_none: detectMultiWindowRapid(s3, JS_RAPID),
  S4_volatility_fallback: detectMultiWindowRapid(s4, JS_RAPID),
};

console.log(JSON.stringify(result, null, 2));
