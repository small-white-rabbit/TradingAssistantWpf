# 跨语言对照基准（surge_pullback）

本目录保存 `SellPointDetectorService.DetectSurgePullback` 的**原版 JS 基准**，
用于验证 C# 翻译后的行为与原版逐行一致。

## 文件
- `verify_surge_js.mjs` —— 自包含的
  `detectSurgePullback` + `getIntervalVolume`，相似度/位置/趋势过滤已桩掉，
  只保留「冲高回落几何判定」核心。与 C# 侧 `DetectSurgePullbackTests` 跑同一组场景。

## 手动对照步骤
1. 运行 JS 基准（需本机有 Node.js）：
   ```bash
   node StockReview.Tests/CrossLanguageBaseline/verify_surge_js.mjs
   ```
2. 运行 C# 翻译版（同一组场景见 `SellPointDetector/DetectSurgePullbackTests.cs`）：
   ```bash
   dotnet test StockReviewWpf.sln --filter "FullyQualifiedName~DetectSurgePullback"
   ```
3. 比对两组输出：
   - S1 冲高回落 → 两侧都应 **FIRE**（peak=103，回落≈40%）
   - S2 无回落 / S3 拉升过小 → 两侧都应 **NULL**

## 约定
- 阈值 `surgePullbackThreshold = 1.8` 两侧一致。
- 若未来调整翻译代码，先跑本对照确认与 JS 基准行为不偏离，再更新 `DetectSurgePullbackTests` 断言。
- 其余检测器（buyPointDetector / multiFactorEngine / patternSimilarity / planScheduler）
  也应按此「抽原 JS + 桩依赖 + 与 C# 真实代码跑同场景」模板逐个补齐对照。

## verify_sell_platformBreakdown_js.mjs（2026-09-02）
1. 运行 JS 基线（2026-09-02 重构版逻辑：分位下轨 + 地板前置检查 + 3 分钟时间确认）：
   ```bash
   node StockReview.Tests/CrossLanguageBaseline/verify_sell_platformBreakdown_js.mjs
   ```
2. 运行 C# 翻译版（同组场景见 `SellPointDetector/DetectPlatformBreakdownTests.cs`）：
   ```bash
   dotnet test StockReviewWpf.sln --filter "FullyQualifiedName~DetectPlatformBreakdown"
   ```
3. 比对两组输出：
   - S1 平台内部高位台阶（301148 实测复现）→ 两侧都应 **NULL**
   - S2 已确立台阶破位 → 两侧都应 **FIRE**（levelPrice=51.30，breakdownPct≈0.487）
   - S3 破位后收回（确认门控不满足）→ 两侧都应 **NULL**

### 约定
- 默认值 `PlatformCandles=180 / PlatformLowerPercentile=15 / PlatformConfirmSnaps=18` 两侧一致。
- 本基线不含形态相似度门控（C# 单测同样以 `EnablePatternSimilarity=false` 跑同场景）。
- 若调整翻译代码，先跑本对照确认行为不偏离，再更新 `DetectPlatformBreakdownTests` 断言。
