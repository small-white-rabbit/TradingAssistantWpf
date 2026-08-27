// VWAP 回踩(vwap_dip) 翻译校验 —— 原 Electron JS 侧（src/stores/buyPointDetector.js:detectVwapDip）
// 与 StockReview.Tests/BuyPointDetector/DetectVwapDipTests.cs 跑同一组场景做跨语言比对。
// 仅抽取 detectVwapDip + getIntervalVolume + avgVolume（桩掉其余买点/三关/评分逻辑）。
// 快照只给 { price, avgPrice, volume }（不设 intervalVolume -> getIntervalVolume 回退 volume），
// 与 C# 侧「不设 IntervalVolume -> GetIntervalVolume 回退 Volume」保持一致。

function getIntervalVolume(snapshot) {
  if (!snapshot) return 0
  const v = snapshot.intervalVolume !== undefined ? snapshot.intervalVolume : snapshot.volume
  return Number(v) || 0
}

function avgVolume(snapshots) {
  if (!snapshots || snapshots.length === 0) return 0
  const sum = snapshots.reduce((acc, s) => acc + getIntervalVolume(s), 0)
  return sum / snapshots.length
}

function detectVwapDip(snapshots, currentPrice, config) {
  const n = snapshots.length
  if (n < 5) return null

  const avgPrice = Number(snapshots[n - 1].avgPrice) || 0
  if (avgPrice <= 0) return null

  const deviationPct = ((avgPrice - currentPrice) / avgPrice) * 100
  if (deviationPct < config.dipToVwapMin) return null
  if (deviationPct > config.dipToVwapThreshold) return null

  const recent = snapshots.slice(-3)
  const belowCount = recent.filter(s => Number(s.price) < Number(s.avgPrice)).length
  if (belowCount < config.dipBelowConfirm) return null

  const prev10 = snapshots.slice(Math.max(0, n - 11), n - 1)
  const prevAvgVol = avgVolume(prev10)
  const currVol = getIntervalVolume(snapshots[n - 1])
  if (prevAvgVol > 0 && currVol > prevAvgVol * config.dipVolumeShrink) return null

  if (currentPrice <= Number(snapshots[n - 2].price)) return null

  return {
    avgPrice,
    deviationPct,
    volumeRatio: prevAvgVol > 0 ? currVol / prevAvgVol : 0,
  }
}

// 默认 BuyConfig 的 VWAP_DIP 相关项
const config = { dipToVwapMin: 0.08, dipToVwapThreshold: 0.5, dipVolumeShrink: 0.8, dipBelowConfirm: 2 }

function build(prices, avg, vols) {
  return prices.map((p, i) => ({ price: p, avgPrice: avg, volume: vols ? vols[i] : 100 }))
}

// S1 回踩均价 + 末根缩量(50) -> 应触发
const s1 = build(
  [100, 100, 100, 100, 100, 100, 100, 100, 100, 99.9, 99.8, 99.9],
  100,
  [100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 50]
)
const r1 = detectVwapDip(s1, 99.9, config)
console.log('[JS] S1:', r1 ? `FIRE avgPrice=${r1.avgPrice} dev=${r1.deviationPct.toFixed(2)} volRatio=${r1.volumeRatio.toFixed(2)}` : 'NULL')

// S2 偏离过小(0.01%) -> 应不触发
const s2 = build([100, 100, 100, 100, 100, 100, 100, 100, 100, 99.99, 99.98, 99.99], 100)
const r2 = detectVwapDip(s2, 99.99, config)
console.log('[JS] S2:', r2 ? 'FIRE' : 'NULL')

// S3 末根放量(200) -> 应不触发
const s3 = build(
  [100, 100, 100, 100, 100, 100, 100, 100, 100, 99.9, 99.8, 99.9],
  100,
  [100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 200]
)
const r3 = detectVwapDip(s3, 99.9, config)
console.log('[JS] S3:', r3 ? 'FIRE' : 'NULL')
