// Cross-language baseline for PatternSimilarityService.
// 方法体逐字抽取自原 Electron 源码 src/stores/patternSimilarity.js（旧仓库兄弟目录）。
// 目的：为「等价子集」(pearson/cosine/dtwDistance 无约束) 提供原 JS 权威数值，
//       用于与 C# PatternSimilarityService 真实代码做跨语言比对。
// 注意：normalize/emaSmooth/resample/dtwSimilarity 在 C# 中是「故意重写」(min-max vs z-score、
//       固定α vs 自适应、resample 是否乘(len-1)、1/(1+d) vs exp(-d*3))，本脚本一并打印供对照，
//       但 C# 回归测试不要求与之相等（属分歧子集）。

class PatternSim {
  constructor() {
    this.dtwWindow = 10;
    this.dtwPsi = 5;
  }

  // ---- 等价子集（与 C# 公式一致，可跨语言比对）----
  pearsonCorrelation(a, b) {
    const n = Math.min(a.length, b.length);
    if (n < 3) return 0;
    const sumA = a.slice(0, n).reduce((s, v) => s + v, 0);
    const sumB = b.slice(0, n).reduce((s, v) => s + v, 0);
    const meanA = sumA / n;
    const meanB = sumB / n;
    let numerator = 0, denomA = 0, denomB = 0;
    for (let i = 0; i < n; i++) {
      const da = a[i] - meanA;
      const db = b[i] - meanB;
      numerator += da * db;
      denomA += da * da;
      denomB += db * db;
    }
    const denominator = Math.sqrt(denomA * denomB);
    if (denominator === 0) return 0;
    return numerator / denominator;
  }

  cosineSimilarity(a, b) {
    if (!a || !b || a.length === 0 || a.length !== b.length) return 0;
    let dot = 0, normA = 0, normB = 0;
    for (let i = 0; i < a.length; i++) {
      dot += a[i] * b[i];
      normA += a[i] * a[i];
      normB += b[i] * b[i];
    }
    const denominator = Math.sqrt(normA) * Math.sqrt(normB);
    if (denominator === 0) return 0;
    const cos = dot / denominator;
    return Math.max(0, cos);
  }

  // window=Infinity, psi=0 时退化为无约束 DTW，与 C# DtwDistance 等价
  dtwDistance(seqA, seqB, window = null, psi = null) {
    const n = seqA.length;
    const m = seqB.length;
    if (n === 0 || m === 0) return Infinity;
    const w = Math.min(window || this.dtwWindow, Math.abs(n - m) + Math.min(n, m));
    const p = Math.min(psi !== null ? psi : this.dtwPsi, Math.floor(Math.min(n, m) / 4));
    let prev = new Array(m + 1).fill(Infinity);
    let curr = new Array(m + 1).fill(Infinity);
    prev[0] = 0;
    for (let j = 1; j <= p; j++) prev[j] = 0;
    for (let i = 1; i <= n; i++) {
      curr[0] = (i <= p) ? 0 : Infinity;
      const jStart = Math.max(1, i - w);
      const jEnd = Math.min(m, i + w);
      for (let j = 1; j <= m; j++) {
        if (j < jStart || j > jEnd) { curr[j] = Infinity; continue; }
        const cost = Math.abs(seqA[i - 1] - seqB[j - 1]);
        curr[j] = cost + Math.min(prev[j], curr[j - 1], prev[j - 1]);
      }
      [prev, curr] = [curr, prev];
    }
    let result = prev[m];
    for (let j = m - 1; j >= Math.max(1, m - p); j--) {
      if (prev[j] < result) result = prev[j];
    }
    return result;
  }

  // ---- 分歧子集（C# 故意重写，仅打印供对照，不作等价断言）----
  normalize(prices) {
    if (!prices || prices.length === 0) return [];
    if (prices.length === 1) return [0.5];
    const validPrices = prices.filter(v => Number.isFinite(v));
    if (validPrices.length === 0) return prices.map(() => 0.5);
    if (validPrices.length === 1) return prices.map(() => 0.5);
    const mean = validPrices.reduce((s, v) => s + v, 0) / validPrices.length;
    const variance = validPrices.reduce((s, v) => s + (v - mean) ** 2, 0) / validPrices.length;
    const std = Math.sqrt(variance);
    if (std === 0) return prices.map(() => 0.5);
    return prices.map(p => {
      if (!Number.isFinite(p)) return 0.5;
      const z = (p - mean) / std;
      return Math.max(0, Math.min(1, (z + 3) / 6));
    });
  }

  emaSmooth(prices, alpha = null) {
    if (!prices || prices.length === 0) return [];
    if (prices.length === 1) return [...prices];
    const a = alpha !== null ? alpha : (prices.length > 30 ? 0.2 : 0.3);
    const result = new Array(prices.length);
    result[0] = prices[0];
    for (let i = 1; i < prices.length; i++) {
      result[i] = a * prices[i] + (1 - a) * result[i - 1];
    }
    return result;
  }

  resample(seq, targetLen) {
    if (!seq || seq.length === 0) return new Array(targetLen).fill(0);
    if (targetLen <= 0) return [];
    if (targetLen === 1) return [seq[0]];
    if (seq.length === targetLen) return [...seq];
    if (seq.length === 1) return new Array(targetLen).fill(seq[0]);
    const result = [];
    for (let i = 0; i < targetLen; i++) {
      const ratio = i / (targetLen - 1);
      const srcIdx = ratio * (seq.length - 1);
      const idx0 = Math.floor(srcIdx);
      const idx1 = Math.min(idx0 + 1, seq.length - 1);
      const frac = srcIdx - idx0;
      result.push(seq[idx0] * (1 - frac) + seq[idx1] * frac);
    }
    return result;
  }

  dtwSimilarity(seqA, seqB) {
    if (!seqA || !seqB || seqA.length < 3 || seqB.length < 3) return 0;
    const distance = this.dtwDistance(seqA, seqB); // 注意：走默认 banded DTW
    if (!Number.isFinite(distance)) return 0;
    const maxLen = Math.max(seqA.length, seqB.length);
    const avgDistance = distance / maxLen;
    const similarity = Math.exp(-avgDistance * 3);
    return Math.max(0, Math.min(1, similarity));
  }
}

const sim = new PatternSim();

const A = [10, 12, 11, 15, 14, 18, 16, 20];
const B = [20, 18, 16, 14, 15, 11, 12, 10];
const C = [10, 10.1, 9.9, 10.05, 10, 9.95, 10.02, 10];

const out = {
  // 等价子集（C# 应与之相等）
  pearson_A_A: sim.pearsonCorrelation(A, A),
  pearson_A_B: sim.pearsonCorrelation(A, B),
  pearson_A_C: sim.pearsonCorrelation(A, C),
  cosine_A_A: sim.cosineSimilarity(A, A),
  cosine_A_B: sim.cosineSimilarity(A, B),
  dtw_A_A_unconstrained: sim.dtwDistance(A, A, Infinity, 0),
  dtw_A_B_unconstrained: sim.dtwDistance(A, B, Infinity, 0),
  dtw_A_C_unconstrained: sim.dtwDistance(A, C, Infinity, 0),
  // 分歧子集（仅供对照，C# 不要求相等）
  normalize_A_js: sim.normalize(A),
  ema_A_js_default: sim.emaSmooth(A),
  resample_A4_js: sim.resample(A, 4),
  dtwSim_A_A_js: sim.dtwSimilarity(A, A),
  dtwSim_A_B_js: sim.dtwSimilarity(A, B),
};
console.log(JSON.stringify(out, null, 2));
