// Cross-language baseline for SellPointDetectorService.DetectHighDeviationPullback。
// 自包含抽原版 detectHighDeviationPullback 方法体
// 关闭 EnablePatternSimilarity（与 C# 测试场景一致）+ 依赖注入，
// 验证「峰值扫描 + 乖离度 + 回落」几何核心。
// 与 C# StockReview.Core/Engines/SellPointDetectorService.DetectHighDeviationPullback 真实代码比对。

function detectHighDeviationPullback(snapshots, currentPrice, config = {}) {
    const C = {
        topPatternMinPosition: 0.5,
        topPatternMaxVwapSlope: 999,
        highDeviationPct: 1.5,
        highDeviationPullback: 0.5,
        enablePatternSimilarity: false,
        ...config
    };
    if (snapshots.length < 5) return null;
    const total = snapshots.length;
    const prices = snapshots.map(s => s.price);

    // 位置过滤：dayLow/High stub（基线用 min/max prices）
    const dayLow = Math.min(...prices);
    const dayHigh = Math.max(...prices);
    const dayRange = dayHigh - dayLow;
    if (dayRange > 0) {
        const currentPosition = (currentPrice - dayLow) / dayRange;
        if (currentPosition < C.topPatternMinPosition) return null;
    }
    // vwapSlope stub（avgPrice 全相同 → 斜率 0）
    const vwapSlope = 0;
    if (vwapSlope > C.topPatternMaxVwapSlope) return null;

    // 峰值扫描（最近 20 根）：同根 peakPrice/peakAvgPrice
    const scanStart = Math.max(0, total - 20);
    let peakPrice = 0, peakAvgPrice = 0, peakIdx = -1;
    for (let i = total - 1; i >= scanStart; i--) {
        const s = snapshots[i];
        if (!s.avgPrice || s.avgPrice <= 0) continue;
        if (s.price > peakPrice) {
            peakPrice = s.price;
            peakAvgPrice = s.avgPrice;
            peakIdx = i;
        }
    }
    if (!peakPrice || !peakAvgPrice || peakAvgPrice <= 0 || peakIdx < 0) return null;

    const deviation = ((peakPrice - peakAvgPrice) / peakAvgPrice) * 100;
    if (deviation < C.highDeviationPct) return null;

    const pullback = ((peakPrice - currentPrice) / peakPrice) * 100;
    if (pullback < C.highDeviationPullback) return null;

    return {
        peakPrice,
        peakAvgPrice,
        deviation,
        pullback,
        levelName: '高乖离回落',
        levelPrice: peakPrice,
        currentPrice,
        isVolumeAmplified: false
    };
}

function buildSnaps(n, priceFn) {
    const arr = [];
    for (let i = 0; i < n; i++) {
        arr.push({
            price: priceFn(i),
            avgPrice: 104.0,  // 全相同 → vwapSlope=0 绕过趋势过滤
            volume: 100,
            intervalVolume: 100,
        });
    }
    return arr;
}

// S1: 触发（peakIdx=10 price=107, deviation≈2.88% >= 1.5%, pullback=2.80% >= 0.5%）
const s1PriceFn = (i) => {
    if (i < 10) return 100 + i;            // 0..9: 100..109? 但 peakPrice 应只 107
    // 改成: 0..9: 100,101,...,109 不对，peak 会取 109
    return 100;
};
// 重设：让 peakPrice=107 only at idx=10
const s1CorrectFn = (i) => {
    if (i < 8) return 100 + i;              // 0..7: 100..107 (price=107 at idx=7)
    if (i === 8) return 106;
    if (i === 9) return 106;
    if (i === 10) return 107;               // peak at idx=10
    if (i <= 12) return 106;
    return 104;                              // 13..29: 回落 104
};
const s1Snaps = buildSnaps(30, s1CorrectFn);
const s1 = detectHighDeviationPullback(s1Snaps, 104);

// S2: 乖离度不够（peak=105, avg=104, deviation=0.96% < 1.5%）
const s2PriceFn = (i) => {
    if (i < 10) return 100 + Math.min(i, 4); // 0..4: 100..104, 5..9: 104
    if (i === 10) return 105;
    return 104;
};
const s2Snaps = buildSnaps(30, s2PriceFn);
const s2 = detectHighDeviationPullback(s2Snaps, 104);

// S3: 回落不足（peak=107, currentPrice=106.5, pullback=0.467% < 0.5%）
const s3Snaps = buildSnaps(30, s1CorrectFn);
const s3 = detectHighDeviationPullback(s3Snaps, 106.5);

console.log(JSON.stringify({
    S1_HighDeviationPullback_Fires: s1 ? {
        levelName: s1.levelName,
        peakPrice: s1.peakPrice,
        peakAvgPrice: s1.peakAvgPrice,
        deviation: s1.deviation,
        pullback: s1.pullback,
    } : null,
    S2_InsufficientDeviation_DoesNotFire: s2,
    S3_InsufficientPullback_DoesNotFire: s3,
}, null, 2));
