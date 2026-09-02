// Cross-language baseline for SellPointDetectorService.DetectVolumeStagnant.
// 自包含抽原版 detectVolumeStagnant 方法体
// 桩掉 this._prepareAnalyzeCtx/calculateVWAPSlope/getIntervalVolume，
// 让 C# 测试场景绕过位置/趋势/距离过滤，聚焦「放量+滞涨」几何核心。
// 期望与 C# StockReview.Core/Engines/SellPointDetectorService.DetectVolumeStagnant 真实代码比对。

function detectVolumeStagnant(snapshots, currentPrice, config = {}) {
    const C = {
        topPatternMinPosition: 0,        // 关闭位置过滤
        topPatternMaxVwapSlope: 999,     // 关闭趋势过滤
        volumeAmplifyMultiple: 2.0,
        stagnantThreshold: 0.5,
        avgPriceDistancePct: 0,          // 关闭距离过滤
        ...config
    };
    if (snapshots.length < 6) return null;
    const prices = snapshots.map(s => s.price);
    const dayLow = Math.min(...prices);
    const dayHigh = Math.max(...prices);
    const dayRange = dayHigh - dayLow;
    if (dayRange > 0) {
        const currentPosition = (currentPrice - dayLow) / dayRange;
        if (currentPosition < C.topPatternMinPosition) return null;
    }
    const vwapSlope = 0; // stub：avgPrice 全相同
    if (vwapSlope > C.topPatternMaxVwapSlope) return null;

    const current = snapshots[snapshots.length - 1];
    const previous = snapshots.slice(-6, -1);
    const avgVolume = previous.reduce((s, x) => s + (x.intervalVolume ?? x.volume ?? 0), 0) / previous.length;
    const currentVol = current.intervalVolume ?? current.volume ?? 0;
    if (!currentVol || avgVolume === 0) return null;
    if (currentVol < avgVolume * C.volumeAmplifyMultiple) return null;

    const recentWindow = Math.min(10, snapshots.length - 1);
    const recentStart = snapshots[snapshots.length - 1 - recentWindow];
    const recentBasePrice = (recentStart && recentStart.price > 0) ? recentStart.price : currentPrice;
    if (!recentBasePrice || recentBasePrice <= 0) return null;
    const recentChangePct = ((currentPrice - recentBasePrice) / recentBasePrice) * 100;
    if (recentChangePct < -0.5 || recentChangePct >= C.stagnantThreshold) return null;

    if (snapshots.length >= 30) {
        const midWindow = Math.min(30, snapshots.length - 1);
        const midStart = snapshots[snapshots.length - 1 - midWindow];
        if (midStart && midStart.price > 0) {
            const midChangePct = ((currentPrice - midStart.price) / midStart.price) * 100;
            if (midChangePct >= 1.5) return null;
        }
    }
    if (snapshots.length >= 60) {
        const longWindow = Math.min(60, snapshots.length - 1);
        const longStart = snapshots[snapshots.length - 1 - longWindow];
        if (longStart && longStart.price > 0) {
            const longChangePct = ((currentPrice - longStart.price) / longStart.price) * 100;
            if (longChangePct >= 2.0) return null;
        }
    }

    const avgPrice = current.avgPrice;
    if (!avgPrice || avgPrice <= 0) return null;
    if (currentPrice <= avgPrice) return null;
    const distancePct = ((currentPrice - avgPrice) / avgPrice) * 100;
    if (distancePct < C.avgPriceDistancePct) return null;

    return {
        levelName: '放量滞涨',
        currentPrice,
        currentVolume: currentVol,
        avgVolume,
        volumeMultiple: currentVol / avgVolume,
        changePct: recentChangePct,
        avgPrice,
        distancePct,
        levelPrice: currentPrice,
        isVolumeAmplified: true
    };
}

// ============ 测试场景 ============
function buildSnaps(n, priceFn, volBase, volLast) {
    const arr = [];
    for (let i = 0; i < n; i++) {
        arr.push({
            price: priceFn(i),
            avgPrice: 103.5,
            volume: volBase,
            intervalVolume: i === n - 1 ? volLast : volBase,
        });
    }
    return arr;
}

// S1: 放量滞涨触发（60根，价格 103→104 涨30根后维持30根，intervalVol 100→300 3x放大）
const s1PriceFn = (i) => i < 30 ? 103 + i / 29 : 104;
const s1Snaps = buildSnaps(60, s1PriceFn, 100, 300);
const s1 = detectVolumeStagnant(s1Snaps, 104);

// S2: 未放量，不触发（currentVol=150，1.5x < 2x 阈值）
const s2Snaps = buildSnaps(60, s1PriceFn, 100, 150);
const s2 = detectVolumeStagnant(s2Snaps, 104);

// S3: 涨太多（不滞涨），不触发（snapshots[49]=103 作 recentWindow 起点，
// snapshots[50..59] 从 103→104 涨约 0.97% >= 0.5% StagnantThreshold → null）
const s3PriceFn = (i) => {
    if (i < 30) return 103 + i / 29;       // 0..29: 103→104
    if (i < 49) return 104;                  // 30..48: 维持 104
    if (i === 49) return 103;                // recentWindow 起点 = 103
    return 103 + (i - 49) / 10;              // 50..59: 103.1→104
};
const s3Snaps = buildSnaps(60, s3PriceFn, 100, 300);
const s3 = detectVolumeStagnant(s3Snaps, 104);

console.log(JSON.stringify({
    S1_HighVolumeStagnant_Fires: s1 ? {
        levelName: s1.levelName,
        volumeMultiple: s1.volumeMultiple,
        changePct: s1.changePct,
        distancePct: s1.distancePct,
        currentVolume: s1.currentVolume,
        avgVolume: s1.avgVolume,
        avgPrice: s1.avgPrice,
    } : null,
    S2_NoVolumeAmplify_DoesNotFire: s2,
    S3_TooMuchRise_DoesNotFire: s3,
}, null, 2));
