// Cross-language baseline for SellPointDetectorService.DetectPlatformBreakdown（跌破平台，2026-09-02 重构版）。
// 自包含抽原 C# 逻辑：分位下轨(PlatformLowerPercentile) + 地板前置检查(preMax) + 时间确认(PlatformConfirmSnaps)。
// 与 C# SellPointDetectorService.DetectPlatformBreakdown（Analyze.cs）及
// StockReview.Tests/SellPointDetector/DetectPlatformBreakdownTests.cs 的两个场景交叉对齐。
// 注：形态相似度门控（EnablePatternSimilarity）不参与本基线，单元测试亦以 false 跑同场景。
// 配置默认值：PlatformCandles=180, PlatformAmplitude=1.5, PlatformBreakdownPct=0.25,
//            PlatformLowerPercentile=15, PlatformConfirmSnaps=18, TopPatternMinPosition=0.3,
//            TopPatternMaxVwapSlope=0.03

function percentile(values, pct) {
    if (values.length === 0) return 0;
    const sorted = [...values].sort((a, b) => a - b);
    let rank = Math.ceil(pct / 100.0 * sorted.length);
    if (rank < 1) rank = 1;
    if (rank > sorted.length) rank = sorted.length;
    return sorted[rank - 1];
}

function calculateSlopeByTime(prices, timestamps) {
    if (!prices || prices.length < 2 || prices.length !== timestamps.length) return 0;
    const n = prices.length;
    const baseTime = timestamps[0];
    const xs = timestamps.map(t => (t - baseTime) / 60000); // ms → 分钟
    let sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
    for (let i = 0; i < n; i++) { sumX += xs[i]; sumY += prices[i]; sumXY += xs[i] * prices[i]; sumX2 += xs[i] * xs[i]; }
    const denom = n * sumX2 - sumX * sumX;
    if (Math.abs(denom) < 1e-10) return 0;
    return (n * sumXY - sumX * sumY) / denom;
}

function calcVwapSlopeRaw(snapshots) {
    if (snapshots.length < 10) return 0;
    const recent = snapshots.slice(snapshots.length - 10);
    const recentValid = recent.filter(s => s.avgPrice > 0);
    if (recentValid.length < 8) return 0;
    const avgPrices = recentValid.map(s => s.avgPrice);
    const timestamps = recentValid.map(s => s.snapshotAt);
    const slope = calculateSlopeByTime(avgPrices, timestamps);
    const startAvg = avgPrices[0];
    if (startAvg <= 0) return 0;
    return slope / startAvg * 100;
}

function prepareCtx(snapshots) {
    let dayLow = Infinity, dayHigh = -Infinity;
    for (const s of snapshots) {
        const p = s.price;
        if (Number.isFinite(p) && p > 0) {
            if (p < dayLow) dayLow = p;
            if (p > dayHigh) dayHigh = p;
        }
        if (Number.isFinite(s.high) && s.high > dayHigh) dayHigh = s.high;
        if (Number.isFinite(s.low) && s.low > 0 && s.low < dayLow) dayLow = s.low;
    }
    if (dayLow === Infinity) dayLow = 0;
    if (dayHigh === -Infinity) dayHigh = 0;
    return { dayLow, dayHigh, vwapSlope: calcVwapSlopeRaw(snapshots) };
}

// 对应 C# DetectPlatformBreakdown（不含形态相似度门控）
function detectPlatformBreakdown(snapshots, currentPrice, config = {}) {
    const C = {
        platformCandles: 180,
        platformAmplitude: 1.5,
        platformBreakdownPct: 0.25,
        platformLowerPercentile: 15,
        platformConfirmSnaps: 18,
        topPatternMinPosition: 0.3,
        topPatternMaxVwapSlope: 0.03,
        ...config
    };
    if (snapshots.length < C.platformCandles + 5) return null;
    const total = snapshots.length;
    const prices = snapshots.map(s => s.price);

    const ctx = prepareCtx(snapshots);
    if (ctx.dayHigh <= ctx.dayLow) return null;
    const dayRange = ctx.dayHigh - ctx.dayLow;
    const currentPosition = (currentPrice - ctx.dayLow) / dayRange;
    if (currentPosition < C.topPatternMinPosition) return null;

    if (ctx.vwapSlope > C.topPatternMaxVwapSlope) return null;

    // 平台前不能是连续下跌
    const prePlatformStart = Math.max(0, total - C.platformCandles - 10);
    const prePlatformEnd = total - C.platformCandles;
    if (prePlatformEnd > prePlatformStart) {
        const prePrices = prices.slice(prePlatformStart, prePlatformEnd);
        const preTrend = (prePrices[prePrices.length - 1] - prePrices[0]) / prePrices[0] * 100;
        if (preTrend < -1) return null;
    }

    const minCandles = Math.min(C.platformCandles, total - 3);
    const maxBack = Math.min(total - minCandles - 3, 240);
    const confirmN = Math.min(C.platformConfirmSnaps, total);

    for (let end = total - 3; end >= 0 && (total - 3 - end) <= maxBack; end--) {
        const start = Math.max(0, end - minCandles + 1);
        const count = end - start + 1;
        if (count < minCandles) continue;
        const seg = prices.slice(start, end + 1);
        const segMax = Math.max(...seg);
        const segMin = Math.min(...seg);
        const mid = (segMax + segMin) / 2;
        if (mid <= 0) continue;
        const amp = (segMax - segMin) / mid * 100;
        if (amp > C.platformAmplitude) continue;

        // 下轨去极值：低分位而非最低价
        const segLow = percentile(seg, C.platformLowerPercentile);
        if (segLow <= 0) continue;

        // 平台必须是已确立的地板：平台窗口之前的近期价格必须到过平台下沿
        const preCount = Math.min(10, start);
        if (preCount > 0) {
            let preMax = -Infinity;
            for (let i = start - preCount; i < start; i++) {
                if (prices[i] > preMax) preMax = prices[i];
            }
            if (preMax < segLow) continue;
        }

        const tail = prices.slice(end + 1);
        if (tail.length < 3) continue;
        const tailMin = Math.min(...tail);
        if (tailMin > segLow) continue;

        const breakdownPct = (segLow - currentPrice) / segLow * 100;
        if (breakdownPct < C.platformBreakdownPct) continue;

        // 时间确认：最近 PlatformConfirmSnaps 个快照持续低于下轨
        let confirmOk = true;
        for (let i = total - confirmN; i < total; i++) {
            if (prices[i] >= segLow) { confirmOk = false; break; }
        }
        if (!confirmOk) continue;

        return {
            levelName: '跌破平台',
            levelPrice: segLow,
            currentPrice,
            platformMax: segMax,
            platformMin: segLow,
            amplitude: amp,
            breakdownPct,
        };
    }
    return null;
}

// 10 秒/根快照构造（对齐单元测试 Mk：avgPrice=price、high=low=price、intervalVolume=100）
function buildSnaps(priceAt, n) {
    const baseTime = Date.parse('2026-09-02T09:30:00+08:00');
    const arr = [];
    for (let i = 0; i < n; i++) {
        const p = priceAt(i);
        arr.push({
            price: p, high: p, low: p, avgPrice: p,
            snapshotAt: baseTime + i * 10000,
            intervalVolume: 100, volume: 100,
        });
    }
    return arr;
}

// S1（= 单测 PriceInsideRealPlatform_ShouldNotFire / 用户实测 301148）：
// 开盘下探 50.80 ×3 → 真实平台 51.00-51.20 震荡 i3..242 → 平台内部高位小台阶 51.30-51.42 i243..282
// → 台阶上回落 51.15/51.12/51.10，现价 51.10 仍在真实平台内。
// 期望 null（台阶窗口不足 180 根无法冒充平台；真实平台下沿 51.00 未被跌破）。
const s1Price = (i) => {
    if (i < 3) return 50.80;
    if (i < 243) return [51.00, 51.05, 51.10, 51.15, 51.20][i % 5];
    if (i < 283) return [51.30, 51.34, 51.38, 51.42][(i - 243) % 4];
    return [51.15, 51.12, 51.10][i - 283];
};
const s1 = detectPlatformBreakdown(buildSnaps(s1Price, 286), 51.10);

// S2（= 单测 EstablishedPlatformBreakdown_ShouldFire）：台阶 51.30-51.42 已确立约 3.5 小时
// （i30..279，台阶前仅 30 根爬升），跌破后 18 根持续低于下轨，现价 51.05。
// 期望触发：levelPrice=51.30（分位下轨=台阶最小价），breakdownPct≈0.487。
const s2Price = (i) => {
    if (i <= 29) return 50.80 + (51.35 - 50.80) * i / 29;
    if (i <= 279) return [51.30, 51.34, 51.38, 51.42][(i - 30) % 4];
    return [51.12, 51.08, 51.05][(i - 280) % 3];
};
const s2 = detectPlatformBreakdown(buildSnaps(s2Price, 298), 51.05);

// S3：同 S2 但跌破后仅 15 根即收回台阶（i295..297 回到 51.30-51.38），
// 最近 18 根未全部低于下轨 → 时间确认不满足，期望 null（过滤瞬间刺穿后收回的假跌破）。
const s3Price = (i) => {
    if (i <= 29) return 50.80 + (51.35 - 50.80) * i / 29;
    if (i <= 279) return [51.30, 51.34, 51.38, 51.42][(i - 30) % 4];
    if (i <= 294) return [51.12, 51.08, 51.05][(i - 280) % 3];
    return [51.30, 51.34, 51.38][i - 295];
};
const s3 = detectPlatformBreakdown(buildSnaps(s3Price, 298), 51.05);

console.log(JSON.stringify({
    S1_ShelfInsideRealPlatform_DoesNotFire: s1,
    S2_EstablishedPlatformBreakdown_Fires: s2 ? {
        levelName: s2.levelName,
        levelPrice: s2.levelPrice,
        breakdownPct: Number(s2.breakdownPct.toFixed(4)),
        amplitude: Number(s2.amplitude.toFixed(4)),
        platformMax: s2.platformMax,
    } : null,
    S3_BreakdownNotConfirmed_DoesNotFire: s3,
}, null, 2));
