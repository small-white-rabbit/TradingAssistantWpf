# 跨语言对照基准（surge_pullback）

本目录保存 `SellPointDetectorService.DetectSurgePullback` 的**原 Electron JS 实现**，
用于验证 C# 翻译后的行为与原版逐行一致。

## 文件
- `verify_surge_js.mjs` —— 从 `src/stores/sellPointDetector.js` 抽取出的
  `detectSurgePullback` + `getIntervalVolume`，相似度/位置/趋势过滤已桩掉，
  只保留「冲高回落几何判定」核心。与 C# 侧 `DetectSurgePullbackTests` 跑同一组场景。

## 手动对照步骤
1. 运行 JS 原版（需本机有 Node.js）：
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
- 若未来调整翻译代码，先跑本对照确认与原 JS 行为不偏离，再更新 `DetectSurgePullbackTests` 断言。
- 其余检测器（buyPointDetector / multiFactorEngine / patternSimilarity / planScheduler）
  也应按此「抽原 JS + 桩依赖 + 与 C# 真实代码跑同场景」模板逐个补齐对照。
