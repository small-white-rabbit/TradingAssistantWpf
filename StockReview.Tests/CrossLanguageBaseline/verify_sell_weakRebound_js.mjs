// Cross-language baseline for SellPointDetectorService.DetectWeakReboundFailure。
// 自包含抽原版 detectWeakReboundFailure 方法体
// 绕 vwapSlope 检查（stub=0, weakReboundVwapSlopeMax=999）+ 间隔量/可靠标志注入，
// 验证 7 条件判定核心：当前下方 + 最近N下方 + 反弹高点 + 高点后回落 + 缩量。
// 与 C# StockReview.Core/Engines/SellPointDetectorService.DetectWeakReboundFailure 真实代码比对。

function detectWeakReboundFailure(snapshots, currentPrice, config = {}) {
    const C = {
        weakReboundBelowConfirm: 5,
        weakReboundMaxScan: 20,
        weakReboundGapMin: -0.3,
        weakReboundGapMax: 0.2,
        weakReboundPullbackPct: 0.5,
        weakReboundVolShrink: 0.6,
        weakReboundVwapSlopeMax: 999,
        ...config
    };
    if (snapshots.length < 10) return null;
    const total = snapshots.length;

    const current = snapshots[total - 1];
    const currentAvg = current.avgPrice || 0;
    if (!currentAvg || currentAvg <= 0) return null;
    if (currentPrice >= currentAvg) return null;

    // 最近 N 根在均价下方
    const recentBelow = snapshots.slice(-C.weakReboundBelowConfirm).every(s => {
        const avg = s.avgPrice || 0;
        return avg > 0 && s.price < avg;
    });
    if (!recentBelow) return null;

    // 回溯找反弹高点（gap ∈ (GapMin, GapMax)）
    const scanStart = Math.max(0, total - C.weakReboundMaxScan);
    let reboundPeak = null;
    for (let i = total - 2; i >= scanStart; i--) {
        const s = snapshots[i];
        const avg = s.avgPrice || 0;
        if (avg <= 0) continue;
        const gap = ((s.price - avg) / avg) * 100;
        if (gap > C.weakReboundGapMin && gap < C.weakReboundGapMax) {
            reboundPeak = { index: i, price: s.price, avgPrice: avg, gap };
            break;
        }
    }
    if (!reboundPeak) return null;

    const afterLen = total - 1 - reboundPeak.index;
    if (afterLen < 3) return null;

    const pullback = ((reboundPeak.price - currentPrice) / reboundPeak.price) * 100;
    if (pullback < C.weakReboundPullbackPct) return null;

    // 缩量：反弹前后均量比
    const reboundWindow = snapshots.slice(
        Math.max(0, reboundPeak.index - 2),
        Math.min(total, reboundPeak.index + 3)
    );
    const reboundAvgVol = reboundWindow.reduce((s, x) => s + (x.intervalVolume ?? x.volume ?? 0), 0) / reboundWindow.length;
    const beforeWindow = snapshots.slice(Math.max(0, reboundPeak.index - 12), Math.max(0, reboundPeak.index - 2));
    const beforeAvgVol = beforeWindow.length > 0
        ? beforeWindow.reduce((s, x) => s + (x.intervalVolume ?? x.volume ?? 0), 0) / beforeWindow.length
        : 0;
    const isVolumeShrink = beforeAvgVol > 0 && reboundAvgVol < beforeAvgVol * C.weakReboundVolShrink;
    if (!isVolumeShrink) return null;

    const vwapSlope = 0; // stub 绕过
    if (vwapSlope > C.weakReboundVwapSlopeMax) return null;

    return {
        levelName: '缩量均线反弹失败',
        levelPrice: reboundPeak.avgPrice,
        currentPrice,
        reboundPrice: reboundPeak.price,
        reboundGap: reboundPeak.gap,
        pullback,
        volumeShrinkRatio: beforeAvgVol > 0 ? reboundAvgVol / beforeAvgVol : 0,
        vwapSlope,
        isVolumeAmplified: false,
        isStopLoss: true
    };
}

function buildSnaps(n, avgPrice, priceFn, beforeVol, reboundVol) {
    const arr = [];
    for (let i = 0; i < n; i++) {
        const iv = i >= 5 && i <= 9 ? reboundVol : beforeVol; // reboundWindow index 5..9 (reboundPeak=7, window=5..9)
        arr.push({
            price: priceFn(i),
            avgPrice,
            volume: beforeVol,
            intervalVolume: iv,
        });
    }
    return arr;
}

const avgPrice = 101;
const priceFn = (i) => {
    if (i < 7) return 100;
    if (i === 7) return 101.1; // 反弹高点
    return 100; // 回落
};

// S1: 触发（最近 5 根下方 + 反弹高点 index=7 + 缩量 50/100=0.5 < 0.6）
const s1Snaps = buildSnaps(15, avgPrice, priceFn, 100, 50);
const s1 = detectWeakReboundFailure(s1Snaps, 100);

// S2: 未缩量（reboundVol=80, beforeVol=100 → 0.8 不 < 0.6）
const s2Snaps = buildSnaps(15, avgPrice, priceFn, 100, 80);
const s2 = detectWeakReboundFailure(s2Snaps, 100);

// S3: 价格在均线上方（currentPrice=102 >= 101 → null 条件1）
const s3 = detectWeakReboundFailure(s1Snaps, 102);

console.log(JSON.stringify({
    S1_WeakReboundFailure_Fires: s1 ? {
        levelName: s1.levelName,
        reboundPrice: s1.reboundPrice,
        reboundGap: s1.reboundGap,
        pullback: s1.pullback,
        volumeShrinkRatio: s1.volumeShrinkRatio,
    } : null,
    S2_NoVolumeShrink_DoesNotFire: s2,
    S3_PriceAboveVWAP_DoesNotFire: s3,
}, null, 2));
