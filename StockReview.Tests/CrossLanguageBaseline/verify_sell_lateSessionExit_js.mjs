// Cross-language baseline for SellPointDetectorService.DetectLateSessionExit。
// 自包含抽原版 detectLateSessionExit 方法体
// 绕时间检查（lateSessionStart='00:00'）+ 依赖注入（间隔量/可靠标志），聚焦「放量+跌破」几何核心。
// 期望与 C# StockReview.Core/Engines/SellPointDetectorService.DetectLateSessionExit 真实代码比对。

function detectLateSessionExit(snapshots, currentPrice, config = {}) {
    const C = {
        lateSessionStart: '00:00',       // 绕时间检查
        lateSessionVolumeMultiple: 2.0,
        lateSessionBreakdownPct: 0.3,
        ...config
    };
    if (snapshots.length < 8) return null;
    const current = snapshots[snapshots.length - 1];
    // 时间检查：基线假设 snapshotAt >= 00:00 永远满足
    if (current.volumeReliable === false) return null;

    const previous = snapshots.slice(-6, -1);
    const avgVolume = previous.reduce((s, x) => s + (x.intervalVolume ?? x.volume ?? 0), 0) / previous.length;
    const currentVol = current.intervalVolume ?? current.volume ?? 0;
    if (!currentVol || avgVolume === 0) return null;
    if (currentVol < avgVolume * C.lateSessionVolumeMultiple) return null;

    const recentPrices = snapshots.slice(-10).map(s => s.price);
    const recentHigh = Math.max(...recentPrices);
    const breakdownPct = ((recentHigh - currentPrice) / recentHigh) * 100;
    if (breakdownPct < C.lateSessionBreakdownPct) return null;

    return {
        levelName: '尾盘资金出逃',
        currentPrice,
        currentVolume: currentVol,
        avgVolume,
        volumeMultiple: currentVol / avgVolume,
        breakdownPct,
        levelPrice: recentHigh,
        isVolumeAmplified: true
    };
}

function buildSnaps(n, priceFn, volBase, volLast) {
    const arr = [];
    for (let i = 0; i < n; i++) {
        arr.push({
            price: priceFn(i),
            avgPrice: priceFn(i),
            volume: volBase,
            intervalVolume: i === n - 1 ? volLast : volBase,
            volumeReliable: true,
        });
    }
    return arr;
}

// S1: 触发（8 根，前 6 根 104、最后 2 根 103.6，跌 0.38% >= 0.3% 阈值，currentVol 300=3x）
const s1PriceFn = (i) => i < 6 ? 104 : 103.6;
const s1 = detectLateSessionExit(buildSnaps(8, s1PriceFn, 100, 300), 103.6);

// S2: 未放量（currentVol=150，1.5x < 2x 阈值）
const s2 = detectLateSessionExit(buildSnaps(8, s1PriceFn, 100, 150), 103.6);

// S3: 跌太少（currentPrice=103.95，跌 0.048% < 0.3% 阈值）
const s3PriceFn = (i) => i < 6 ? 104 : 103.95;
const s3 = detectLateSessionExit(buildSnaps(8, s3PriceFn, 100, 300), 103.95);

console.log(JSON.stringify({
    S1_LateSessionExit_Fires: s1 ? {
        levelName: s1.levelName,
        volumeMultiple: s1.volumeMultiple,
        breakdownPct: s1.breakdownPct,
        currentVolume: s1.currentVolume,
        avgVolume: s1.avgVolume,
        levelPrice: s1.levelPrice,
    } : null,
    S2_NoVolumeAmplify_DoesNotFire: s2,
    S3_TooSmallBreakdown_DoesNotFire: s3,
}, null, 2));
