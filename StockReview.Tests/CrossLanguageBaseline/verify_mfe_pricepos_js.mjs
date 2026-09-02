// 价格位置因子(pricePosition) 翻译校验 —— 原版 JS 基准
// 与 StockReview.Tests/MultiFactor/ExtractPricePositionFactorTests.cs 跑同一组场景做跨语言比对。
// 仅抽取 extractPricePositionFactor（桩掉其余因子/综合评分/权重库）。
// 快照用 { price, avgPrice }（last.avgPrice 用于乖离率，price 序列用于日内高低位分位）。

function extractPricePositionFactor(snapshots, currentPrice) {
  const prices = snapshots.map(s => s.price).filter(p => p > 0)
  if (prices.length < 2)
    return { key: 'pricePosition', name: '价格位置', score: 0, direction: 'neutral', detail: '数据不足' }

  const dayLow = Math.min(...prices)
  const dayHigh = Math.max(...prices)
  const range = dayHigh - dayLow
  const position = range > 0 ? (currentPrice - dayLow) / range : 0.5

  const avgPrice = Number(snapshots[snapshots.length - 1].avgPrice) || 0
  const deviationPct = avgPrice > 0 ? ((currentPrice - avgPrice) / avgPrice) * 100 : 0

  let score = 0
  let direction = 'neutral'
  const reasons = []

  if (position > 0.8) { score += 40; direction = 'bear'; reasons.push(`日内高位(${(position * 100).toFixed(0)}%)`) }
  else if (position > 0.6) { score += 20; direction = 'bear'; reasons.push(`偏高位(${(position * 100).toFixed(0)}%)`) }
  else if (position < 0.2) { score += 30; direction = 'bull'; reasons.push(`日内低位(${(position * 100).toFixed(0)}%)`) }

  if (deviationPct > 2.0) { score += 35; direction = 'bear'; reasons.push(`高乖离${deviationPct.toFixed(1)}%`) }
  else if (deviationPct > 1.0) { score += 15; if (direction !== 'bear') direction = 'bear'; reasons.push(`乖离${deviationPct.toFixed(1)}%`) }

  return {
    key: 'pricePosition',
    name: '价格位置',
    score: Math.min(100, score),
    direction,
    detail: reasons.join(' + ') || `位置${(position * 100).toFixed(0)}%, 乖离${deviationPct.toFixed(1)}%`,
  }
}

function mk(price, avg) { return { price, avgPrice: avg } }

// S1 日内高位(0.9) + 高乖离(5.6%) -> bear, score 75
const s1 = [mk(10, 18), mk(15, 18), mk(20, 18), mk(19, 18)]
const r1 = extractPricePositionFactor(s1, 19)
console.log(`[JS] S1: score=${r1.score} dir=${r1.direction} detail="${r1.detail}"`)

// S2 日内低位(0.1) -> bull, score 30
const s2 = [mk(10, 11), mk(12, 11), mk(11, 11), mk(10.2, 11)]
const r2 = extractPricePositionFactor(s2, 10.2)
console.log(`[JS] S2: score=${r2.score} dir=${r2.direction} detail="${r2.detail}"`)

// S3 数据不足(仅1根) -> neutral, score 0
const s3 = [mk(10, 11)]
const r3 = extractPricePositionFactor(s3, 10)
console.log(`[JS] S3: score=${r3.score} dir=${r3.direction} detail="${r3.detail}"`)
