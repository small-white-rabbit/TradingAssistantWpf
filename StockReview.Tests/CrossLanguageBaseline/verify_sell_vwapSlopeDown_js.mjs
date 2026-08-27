// Cross-language baseline for SellPointDetectorService.DetectVWAPSlopeDown。
// 自包含抽 sellPointDetector.js:detectVWAPSlopeDown 方法体（2941-2975）+ calculateSlopeByTime（3424-3440）
// 与 C# StockReview.Core/Engines/SellPointDetectorService.DetectVWAPSlopeDown/CalculateSlopeByTime 真实代码比对。
// calculateSlopeByTime 两边均为 OLS 线性回归：slope = (n*Σxy - Σx*Σy) / (n*Σx² - Σx²)

function calculateSlopeByTime(prices, timestamps) {
    if (!prices || prices.length < 2 || prices.length !== timestamps.length) return 0;
    const n = prices.length;
    const baseTime = new Date(timestamps[0]).getTime();
    if (!Number.isFinite(baseTime)) return 0;
    const xs = timestamps.map(t => {
        const ms = new Date(t).getTime();
        return Number.isFinite(ms) ? (ms - baseTime) / 60000 : 0;
    });
    const sumX = xs.reduce((a, b) => a + b, 0);
    const sumY = prices.reduce((a, b) => a + b, 0);
    const sumXY = xs.reduce((s, x, i) => s + x * prices[i], 0);
    const sumX2 = xs.reduce((s, x) => s + x * x, 0);
    const denom = n * sumX2 - sumX * sumX;
    if (Math.abs(denom) < 1e-10) return 0;
    return (n * sumXY - sumX * sumY) / denom;
}

function detectVWAPSlopeDown(snapshots, currentPrice, config = {}) {
    const C = {
        vwapSlopeDownCandles: 5,
        vwapSlopeDownThreshold: -0.1,
        ...config
    };
    if (snapshots.length < C.vwapSlopeDownCandles + 3) return null;
    const windowSize = Math.max(C.vwapSlopeDownCandles + 3, 8);
    const recent = snapshots.slice(-windowSize);

    const recentValid = recent.filter(s => s.avgPrice > 0);
    if (recentValid.length < C.vwapSlopeDownCandles) return null;

    const slice = recentValid.slice(-C.vwapSlopeDownCandles);
    const slicePrices = slice.map(s => s.avgPrice);
    const sliceTimestamps = slice.map(s => s.snapshotAt);
    const startAvg = slicePrices[0];
    if (!startAvg || startAvg <= 0) return null;

    const rawSlope = calculateSlopeByTime(slicePrices, sliceTimestamps);
    const slope = (rawSlope / startAvg) * 100;
    if (slope >= C.vwapSlopeDownThreshold) return null;

    const currentAvg = recentValid.length > 0 ? recentValid[recentValid.length - 1].avgPrice : 0;
    if (!currentAvg || currentPrice >= currentAvg) return null;

    return {
        slope,
        currentAvg,
        levelName: '均价线拐头向下',
        levelPrice: currentAvg,
        currentPrice,
        isVolumeAmplified: false
    };
}

function buildSnaps(n, baseTime, avgPriceFn, priceFn) {
    const arr = [];
    for (let i = 0; i < n; i++) {
        const t = new Date(baseTime.getTime() + i * 60 * 1000);
        arr.push({
            price: priceFn(i),
            avgPrice: avgPriceFn(i),
            volume: 100,
            intervalVolume: 100,
            snapshotAt: t.toISOString(),
        });
    }
    return arr;
}

// S1: 触发（8根，avgPrice 从 105→101.5 线性下降 -0.5/min，slope≈-0.483%/min < -0.1）
const baseTime = new Date('2026-01-01T14:30:00+08:00');
const s1AvgFn = (i) => 105 - 0.5 * i;
const s1PriceFn = (i) => 100;
const s1Snaps = buildSnaps(8, baseTime, s1AvgFn, s1PriceFn);
const s1 = detectVWAPSlopeDown(s1Snaps, 100);

// S2: 斜率为 0（avgPrice 全=105），slope=0 >= -0.1 → null
const s2AvgFn = () => 105;
const s2Snaps = buildSnaps(8, baseTime, s2AvgFn, () => 100);
const s2 = detectVWAPSlopeDown(s2Snaps, 100);

// S3: 触发斜率但 currentPrice >= currentAvg → null（avgPrice 从 110→108 但 currentPrice=109）
const s3AvgFn = (i) => {
    const base = [110, 109.5, 109, 108.5, 108, 107.5, 107, 108];
    return base[i];
};
const s3Snaps = buildSnaps(8, baseTime, s3AvgFn, (i) => i === 7 ? 109 : 100);
const s3 = detectVWAPSlopeDown(s3Snaps, 109);

console.log(JSON.stringify({
    S1_VWAPSlopeDown_Fires: s1 ? {
        levelName: s1.levelName,
        slope: s1.slope,
        currentAvg: s1.currentAvg,
    } : null,
    S2_NoSlopeDown_DoesNotFire: s2,
    S3_PriceAboveVWAP_DoesNotFire: s3,
}, null, 2));
