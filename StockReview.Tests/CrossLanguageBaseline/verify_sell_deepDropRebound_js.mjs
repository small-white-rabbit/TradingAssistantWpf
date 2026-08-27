// Cross-language baseline for SellPointDetectorService.DetectDeepDropRebound。
// 自包含抽 sellPointDetector.js:detectDeepDropRebound 方法体（3264-?）+ _findPlatformBefore（3378-3401）
// 与 C# StockReview.Core/Engines/SellPointDetectorService.DetectDeepDropRebound/FindPlatformBefore 真实代码比对。

function findPlatformBefore(snapshots, endIndex, config) {
    const minBars = config.deepDropPlatformMinBars;
    const ampMax = config.deepDropPlatformAmplitude;
    const searchEnd = Math.min(endIndex - 1, snapshots.length - 1);
    for (let end = searchEnd; end >= minBars - 1; end--) {
        const start = end - minBars + 1;
        let segMax = -Infinity, segMin = Infinity;
        let valid = true;
        for (let i = start; i <= end; i++) {
            const p = Number(snapshots[i].price);
            if (!Number.isFinite(p)) { valid = false; break; }
            if (p > segMax) segMax = p;
            if (p < segMin) segMin = p;
        }
        if (!valid) continue;
        const mid = (segMax + segMin) / 2;
        if (mid <= 0) continue;
        const amp = ((segMax - segMin) / mid) * 100;
        if (amp < ampMax) {
            return { start, end, top: segMax, bottom: segMin };
        }
    }
    return null;
}

function detectDeepDropRebound(snapshots, currentPrice, config = {}) {
    const C = {
        deepDropMinSnapshots: 10,
        deepDropMinPct: 5,
        deepDropReboundMinPct: 2,
        deepDropAboveVwapTol: 0.5,
        deepDropPlatformMinBars: 5,
        deepDropPlatformAmplitude: 1.0,
        deepDropTouchTolerance: 1.0,
        deepDropVolShrink: 0.6,
        deepDropMaxElapsed: 30,
        deepDropPullbackPct: 0.5,
        ...config
    };
    if (snapshots.length < C.deepDropMinSnapshots) return null;
    const total = snapshots.length;
    const last = snapshots[total - 1];
    const basePrice = (last.preClose) || (snapshots[0].preClose);
    if (!basePrice || basePrice <= 0) return null;

    // 日内最低点
    let lowIdx = -1, lowPrice = Infinity;
    for (let i = 0; i < total; i++) {
        const p = Number(snapshots[i].price);
        if (Number.isFinite(p) && p < lowPrice) { lowPrice = p; lowIdx = i; }
    }
    if (lowIdx < 0) return null;
    const dropPct = ((lowPrice - basePrice) / basePrice) * 100;
    if (dropPct > -C.deepDropMinPct) return null;

    // 反弹高点
    let reboundIdx = -1, reboundHigh = -Infinity;
    for (let i = lowIdx + 1; i < total; i++) {
        const p = Number(snapshots[i].price);
        if (Number.isFinite(p) && p > reboundHigh) { reboundHigh = p; reboundIdx = i; }
    }
    if (reboundIdx < 0 || reboundHigh <= lowPrice) return null;
    const reboundPct = ((reboundHigh - lowPrice) / lowPrice) * 100;
    if (reboundPct < C.deepDropReboundMinPct) return null;

    // 反抽过均线
    const reboundAvg = Number(snapshots[reboundIdx]?.avgPrice) || 0;
    if (reboundAvg > 0 && reboundHigh < reboundAvg * (1 - C.deepDropAboveVwapTol / 100)) return null;

    // 触及平台
    const platform = findPlatformBefore(snapshots, reboundIdx, C);
    let touchedPlatform = null;
    if (platform) {
        const nearTop = Math.abs(reboundHigh - platform.top) / platform.top * 100 <= C.deepDropTouchTolerance;
        const nearBottom = Math.abs(reboundHigh - platform.bottom) / platform.bottom * 100 <= C.deepDropTouchTolerance;
        const inside = reboundHigh >= platform.bottom && reboundHigh <= platform.top;
        if (!nearTop && !nearBottom && !inside) return null;
        touchedPlatform = nearTop ? 'top' : (nearBottom ? 'bottom' : 'inside');
    }

    // 末端缩量（prevSeg < 6 时跳过检查，不强制）
    let isVolumeShrink = null;
    if (snapshots[total - 1]?.volumeReliable !== false) {
        const tailStart = Math.max(lowIdx + 1, reboundIdx - 7);
        const tailEnd = Math.min(reboundIdx + 1, total);
        const tailSeg = snapshots.slice(tailStart, tailEnd);
        const prevStart = Math.max(lowIdx + 1, tailStart - 12);
        const prevSeg = snapshots.slice(prevStart, tailStart);
        if (tailSeg.length >= 4 && prevSeg.length >= 6) {
            const tailAvg = tailSeg.reduce((s, x) => s + (x.intervalVolume ?? x.volume ?? 0), 0) / tailSeg.length;
            const prevAvg = prevSeg.reduce((s, x) => s + (x.intervalVolume ?? x.volume ?? 0), 0) / prevSeg.length;
            if (prevAvg > 0) {
                isVolumeShrink = tailAvg < prevAvg * C.deepDropVolShrink;
                if (!isVolumeShrink) return null;
            }
        }
    }

    const afterLen = total - 1 - reboundIdx;
    if (afterLen < 3) return null;
    if (afterLen > C.deepDropMaxElapsed) return null;

    const pullbackPct = ((reboundHigh - currentPrice) / reboundHigh) * 100;
    if (pullbackPct < C.deepDropPullbackPct) return null;

    return {
        levelName: '大跌反抽卖点',
        levelPrice: reboundHigh,
        currentPrice,
        dropPct,
        reboundPct,
        pullbackPct,
        lowPrice,
        reboundAboveVwap: reboundAvg > 0,
        touchedPlatform,
        platformTop: platform?.top,
        platformBottom: platform?.bottom,
        isVolumeShrink,
    };
}

function buildSnaps(n, baseTime, priceFn, avgPriceFn, preClose) {
    const arr = [];
    for (let i = 0; i < n; i++) {
        arr.push({
            price: priceFn(i),
            avgPrice: avgPriceFn(i),
            preClose,
            volume: 100,
            intervalVolume: 100,
            volumeReliable: true,
        });
    }
    return arr;
}

const baseTime = new Date('2026-01-01T09:30:00+08:00');

// S1: 触发（lowPrice=93 -7%、平台[3..7]=95、reboundHigh=95.5 at idx=8、过均线、touchPlatform=top、pullback 1.57%）
const s1PriceFn = (i) => {
    if (i < 3) return 93;
    if (i < 8) return 95;       // 平台
    if (i === 8) return 95.5;   // 反弹高点
    return 94;                  // 回落
};
const s1AvgPriceFn = (i) => {
    if (i < 3) return 93;
    if (i < 8) return 94;
    if (i === 8) return 94.5;
    return 94.5;
};
const s1Snaps = buildSnaps(15, baseTime, s1PriceFn, s1AvgPriceFn, 100);
const s1 = detectDeepDropRebound(s1Snaps, 94);

// S2: 未深跌（lowPrice=98 -2%，dropPct > -5% → null）
const s2PriceFn = (i) => i < 3 ? 98 : 99;
const s2AvgPriceFn = (i) => i < 3 ? 98 : 99;
const s2Snaps = buildSnaps(15, baseTime, s2PriceFn, s2AvgPriceFn, 100);
const s2 = detectDeepDropRebound(s2Snaps, 99);

// S3: 反弹不足（reboundHigh=94.5 来自 idx=8，reboundPct=1.61% < 2% → null；
//     平台[3..7]=94 < 反弹高点 94.5，确保 reboundHigh 不被平台捕获）
const s3PriceFn = (i) => {
    if (i < 3) return 93;
    if (i < 8) return 94;       // 平台 94 < 反弹高点 94.5
    if (i === 8) return 94.5;  // 反弹高点（但涨幅 1.61% 不够）
    return 94;
};
const s3Snaps = buildSnaps(15, baseTime, s3PriceFn, s1AvgPriceFn, 100);
const s3 = detectDeepDropRebound(s3Snaps, 94);

console.log(JSON.stringify({
    S1_DeepDropRebound_Fires: s1 ? {
        levelName: s1.levelName,
        dropPct: s1.dropPct,
        reboundPct: s1.reboundPct,
        pullbackPct: s1.pullbackPct,
        lowPrice: s1.lowPrice,
        touchedPlatform: s1.touchedPlatform,
    } : null,
    S2_NotDeepDrop_DoesNotFire: s2,
    S3_InsufficientRebound_DoesNotFire: s3,
}, null, 2));
