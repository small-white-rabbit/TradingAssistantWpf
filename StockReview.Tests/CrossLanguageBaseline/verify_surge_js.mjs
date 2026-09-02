// 冲高回落(surge_pullback) 翻译校验 —— 原版 JS 侧
// 自包含的 detectSurgePullback + 最小依赖桩。
// 与 C# StockReview.Core 跑同一组场景，做跨语言比对。
// 注意：两侧都关闭 位置过滤 / 趋势过滤 / 形态相似度过滤，以隔离「冲高回落几何判定」这一翻译核心。

const config = {
  surgePullbackThreshold: 1.8,
  pullbackRatio: 0.35,
  surgeFastSpan: 3,
  surgeFastMinRisePct: 1.2,
  topPatternMinPosition: -1,        // 关闭位置过滤
  topPatternMaxVwapSlope: Infinity, // 关闭趋势过滤
  enablePatternSimilarity: false,   // 关闭相似度过滤（与 C# 传 null 计算器等价）
  surgePullbackSimilarityMin: 0.45,
};

class Detector {
  constructor(cfg) { this.config = cfg; }

  getIntervalVolume(s) { return s.intervalVolume ?? s.volume; }

  // 最小桩：仅算 dayLow/dayHigh（用于位置过滤，本测试已关闭）
  _prepareAnalyzeCtx(snapshots) {
    let dayLow = Infinity, dayHigh = -Infinity;
    for (const s of snapshots) {
      const p = s.price;
      if (Number.isFinite(p) && p > 0) { if (p < dayLow) dayLow = p; if (p > dayHigh) dayHigh = p; }
      if (Number.isFinite(s.high) && s.high > dayHigh) dayHigh = s.high;
      if (Number.isFinite(s.low) && s.low > 0 && s.low < dayLow) dayLow = s.low;
    }
    if (dayLow === Infinity) dayLow = 0;
    if (dayHigh === -Infinity) dayHigh = 0;
    return { dayLow, dayHigh };
  }
  calculateVWAPSlope() { return 0; }       // 桩
  checkVolumeAmplified() { return false; } // 桩

  // ===== 以下为正文，逐行对应原版方法体 =====
  detectSurgePullback(snapshots, currentPrice) {
    if (snapshots.length < 6) return null;
    const prices = snapshots.map(s => s.price);
    const volumes = snapshots.map(s => this.getIntervalVolume(s));
    const total = prices.length;
    const basePriceGlobal = snapshots[snapshots.length - 1]?.preClose || snapshots[0]?.preClose || snapshots[0]?.price;

    const dayLow = this._prepareAnalyzeCtx(snapshots).dayLow;
    const dayHigh = this._prepareAnalyzeCtx(snapshots).dayHigh;
    const dayRange = dayHigh - dayLow;
    if (dayRange > 0) {
      const currentPosition = (currentPrice - dayLow) / dayRange;
      if (currentPosition < this.config.topPatternMinPosition) return null;
    }

    const vwapSlope = this.calculateVWAPSlope(snapshots);
    if (vwapSlope > this.config.topPatternMaxVwapSlope) return null;

    const scanStart = Math.max(2, total - 25);
    let bestPeakIdx = -1, bestBasePrice = 0, bestSurgeAbs = 0;
    const fastSpan = this.config.surgeFastSpan || 3;
    const fastMinRisePct = this.config.surgeFastMinRisePct || 1.2;
    const fastPullbackRatio = Math.max(this.config.pullbackRatio, 0.5);

    for (let p = total - 2; p >= scanStart; p--) {
      if (prices[p] <= prices[p + 1]) continue;
      const upStart = Math.max(0, p - 12);
      const upLegPrices = prices.slice(upStart, p + 1);
      const upLegLow = Math.min(...upLegPrices);
      const surgeAbs = prices[p] - upLegLow;
      if (surgeAbs <= 0) continue;
      const basePrice = snapshots[upStart]?.preClose || basePriceGlobal;
      if (!basePrice || basePrice <= 0) continue;
      const surgePct = (surgeAbs / basePrice) * 100;
      let fastPass = false;
      if (p + 1 >= fastSpan) {
        const fastSlice = prices.slice(Math.max(0, p + 1 - fastSpan), p + 1);
        const fastLow = Math.min(...fastSlice);
        const fastRise = (prices[p] - fastLow) / (basePrice || fastLow) * 100;
        if (fastRise >= fastMinRisePct) fastPass = true;
      }
      if (surgePct < this.config.surgePullbackThreshold && !fastPass) continue;
      const downLegPrices = prices.slice(p + 1);
      if (downLegPrices.length < 2) continue;
      const downLegLow = Math.min(...downLegPrices);
      const finalPrice = currentPrice || downLegPrices[downLegPrices.length - 1];
      const actualTrough = Math.min(downLegLow, finalPrice);
      const pullbackAbs = prices[p] - actualTrough;
      if (pullbackAbs <= 0) continue;
      const pullbackRatio = pullbackAbs / surgeAbs;
      const minPullback = fastPass ? fastPullbackRatio : this.config.pullbackRatio;
      if (pullbackRatio < minPullback) continue;
      bestPeakIdx = p; bestBasePrice = basePrice; bestSurgeAbs = surgeAbs; break;
    }

    let intraBar = null;
    if (bestPeakIdx < 0) {
      const fHigh = Number(snapshots[total - 1]?.high);
      const prevClose = prices[total - 2];
      if (Number.isFinite(fHigh) && fHigh > 0 && Number.isFinite(prevClose) && fHigh > prevClose && fHigh > currentPrice) {
        const upStart = Math.max(0, total - 1 - 12);
        const upLegPrices = prices.slice(upStart, total - 1);
        const upLegLow = upLegPrices.length ? Math.min(...upLegPrices) : prevClose;
        const surgeAbs = fHigh - upLegLow;
        const basePrice = snapshots[upStart]?.preClose || basePriceGlobal;
        if (surgeAbs > 0 && basePrice > 0) {
          const surgePct = (surgeAbs / basePrice) * 100;
          const fastSlice = prices.slice(Math.max(0, total - 1 - fastSpan), total - 1);
          const fastLow = fastSlice.length ? Math.min(...fastSlice) : prevClose;
          const fastRise = (fHigh - fastLow) / (basePrice || fastLow) * 100;
          const fastPass = fastRise >= fastMinRisePct;
          const pullbackAbs = fHigh - currentPrice;
          const pullbackRatio = pullbackAbs / surgeAbs;
          const minPullback = fastPass ? fastPullbackRatio : this.config.pullbackRatio;
          if ((surgePct >= this.config.surgePullbackThreshold || fastPass) && pullbackRatio >= minPullback) {
            bestPeakIdx = total - 1; bestBasePrice = basePrice; bestSurgeAbs = surgeAbs; intraBar = { high: fHigh };
          }
        }
      }
    }

    if (bestPeakIdx < 0) return null;
    const peakPrice = intraBar ? intraBar.high : prices[bestPeakIdx];
    const downLegPrices = prices.slice(bestPeakIdx + 1);
    const downLegLow = Math.min(...downLegPrices);
    const finalPrice = currentPrice || downLegPrices[downLegPrices.length - 1];
    const actualTrough = Math.min(downLegLow, finalPrice);
    const pullbackAbs = peakPrice - actualTrough;
    const pullbackRatio = pullbackAbs / bestSurgeAbs;
    const currentChangePct = ((currentPrice - bestBasePrice) / bestBasePrice) * 100;
    const peakChangePct = ((peakPrice - bestBasePrice) / bestBasePrice) * 100;
    return { peakPrice, peakChangePct, surgeAbs: bestSurgeAbs, pullbackAbs, pullbackRatio: pullbackRatio * 100, currentChangePct, intraBar: !!intraBar };
  }
}

// ===== 场景（与 C# 侧完全一致） =====
function mk(prices, preClose) {
  const t0 = Date.parse('2026-01-01T09:30:00');
  return prices.map((p, i) => ({
    price: p, preClose, high: p, low: p, volume: 1000, intervalVolume: 100,
    avgPrice: p, snapshotAt: new Date(t0 + i * 60000),
  }));
}

const det = new Detector(config);
const scenarios = [
  { name: 'S1 冲高回落(应触发)', prices: [100,100.2,100.4,100.6,100.8,101,102,103,103,103,102.5,101.8], cur: 101.8 },
  { name: 'S2 无回落(应不触发)',  prices: [100,100.2,100.4,100.6,100.8,101,102,103,103,103,103,103],   cur: 103.0 },
  { name: 'S3 拉升过小(应不触发)', prices: [100,100.1,100.2,100.3,100.4,100.5,100.6,100.7,100.8,100.9,101,100.5], cur: 100.5 },
];

for (const sc of scenarios) {
  const snaps = mk(sc.prices, 100);
  const r = det.detectSurgePullback(snaps, sc.cur);
  if (!r) { console.log(`[JS] ${sc.name}: NULL (未触发)`); continue; }
  console.log(`[JS] ${sc.name}: FIRE peak=${r.peakPrice.toFixed(2)} peakChg=${r.peakChangePct.toFixed(2)}% pullbackRatio=${r.pullbackRatio.toFixed(2)}% curChg=${r.currentChangePct.toFixed(2)}% intraBar=${r.intraBar}`);
}
