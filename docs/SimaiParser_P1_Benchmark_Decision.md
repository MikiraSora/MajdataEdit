# SimaiParser P1 候选方案 Benchmark 决策

状态：三个 P1 的候选方案已完成隔离 Benchmark；2026-07-24 已按决策落地生产解析器，并完成多目标构建、22 项语义验证和端到端 ShortRun 复测。

## 决策摘要

| P1 | 性能首选 | 低风险备选 | 最大规模实测 |
| --- | --- | --- | --- |
| 文本位置 | 顺序游标，检测到逆序查询时二分回退 | 全部查询使用二分 | 4096 Timing：当前 `5.779 ms`，二分 `242.865 us`，游标 `10.250 us` |
| HSpeed | 对已排序事件和 Timing 做顺序 Sweep，预计算速度数组后保留并行 Note 解析 | 建按 group 索引后在现有 `Parallel.For` 中二分 | 2048 事件：当前 `765.901-937.524 us`，二分 `86.330-96.729 us`，Sweep `74.604-75.975 us` |
| RawContent | 首次规范化后在 HSpeed 回填和最终 Timing 中复用同一 immutable string | 只消除 HSpeed 回填时的第二次规范化 | 4096 Timing：当前 `439.851 us / 632.08 KB`，部分复用 `260.565 us / 488.08 KB`，完整复用 `177.345 us / 344.08 KB` |

基于本次数据，已选择并落地：

1. 文本位置采用“顺序游标 + 二分回退”。
2. HSpeed 采用“顺序 Sweep 预计算 + 保留现有并行 Note 解析”。
3. RawContent 采用完整复用，但继续为 `SimaiNote.RawContent` 保留独立字符串；Note 内容并不总与 Timing 内容相同。

## 测量边界

新增基准位于 `MajSimaiX/Benchmark~/P1StrategyBenchmarks.cs`，包含四个类：

- `P1TextPositionStrategyBenchmarks`
- `P1HSpeedStrategyBenchmarks`
- `P1HSpeedFinalizationStrategyBenchmarks`
- `P1RawContentStrategyBenchmarks`

这些是从优化前源码提取的隔离内核，用于比较算法；它们与生产解析器的端到端复测分开记录。优化前完整解析器基线为：4096 Timing 多行谱面 `14.841 ms`、紧凑谱面 `1.698 ms`；2048 Timing 的每槽 SV 谱面 `3.678 ms`、普通谱面 `832.36 us`。

所有候选在 `GlobalSetup` 中先执行等价断言：

- 位置方案对全部查询的 `(x, y)` 混合校验和相同。
- HSpeed 方案对全部查询的 IEEE 754 float bit 校验和相同，覆盖 1/8 group、同一时间的多 group 事件、负数和零速度。
- RawContent 方案逐项比较 Timing 与 Note 字符串，输入混合 lowercase `c`、空白、换行、Slide 和 FixedSoflan `@600`。

环境与总审计相同：Ryzen 7 5800X、.NET 8.0.27、BenchmarkDotNet 0.15.8、`ShortRun`（3 次预热、3 次正式迭代）。绝对耗时不可跨机器比较，方案比值和复杂度趋势可用于当前实现决策。

复现全部 P1：

```powershell
dotnet run --project 'MajSimaiX\Benchmark~\MajSimai.Benchmarks.csproj' -c Release -- --job Short --filter '*P1*'
```

只复现生产形态 HSpeed 对比：

```powershell
dotnet run --project 'MajSimaiX\Benchmark~\MajSimai.Benchmarks.csproj' -c Release -- --job Short --filter '*P1HSpeedFinalizationStrategyBenchmarks*'
```

## 生产落地与端到端复测

生产代码采用以下边界：

- 文本位置在 offset 单调不减时推进换行游标，offset 回退时执行 upper-bound 二分并用结果重置游标。
- HSpeed 在最终事件表和 Raw Timing 表排序、去重后顺序 Sweep，按 group 保存当前速度；有 HSpeed 事件时生成速度数组，随后原有 `Parallel.For` 只解析 Note。
- RawContent 仅在初次 `SimaiRawTimingPoint` 构造时执行 lowercase `c`、空白和 FixedSoflan 间距处理。HSpeed 回填复用该 string，最终 `SimaiTimingPoint` 通过 internal trusted path 接收同一 string；公开构造函数仍执行原有规范化。

### 多行布局

| Timing | 旧 Compact | 新 Compact | 旧多行 | 新多行 | 旧比值 | 新比值 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 256 | 117.0 us | 111.7 us | 176.2 us | 114.0 us | 1.51x | 1.02x |
| 1024 | 383.9 us | 360.5 us | 1.252 ms | 375.8 us | 3.26x | 1.04x |
| 4096 | 1.698 ms | 1.600 ms | 14.841 ms | 1.675 ms | 8.74x | 1.05x |

4096 Timing 多行场景耗时下降约 `88.7%`，多行/紧凑比值不再随行数呈二次增长。紧凑场景没有 HSpeed 数组，约 `5.8%` 的降幅及从 `3174.85 KB` 到 `2983.00 KB` 的分配下降主要来自 RawContent 完整复用。

### HSpeed/SV 密度

| Timing | 旧 Plain | 新 Plain | 旧每槽 SV | 新每槽 SV | SV 降幅 | 旧比值 | 新比值 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 128 | 70.18 us | 66.10 us | 154.93 us | 128.23 us | 17.2% | 2.21x | 1.94x |
| 512 | 200.22 us | 195.72 us | 564.69 us | 442.78 us | 21.6% | 2.82x | 2.26x |
| 2048 | 832.36 us | 766.55 us | 3.678 ms | 2.095 ms | 43.0% | 4.42x | 2.73x |

2048 Timing 的每槽 SV 分配从 `3200.77 KB` 降至 `3120.22 KB`。这是 RawContent 少分配字符串与 Sweep 多分配速度数组后的净值，不能据此认为 Sweep 本身减少内存。

### 已知副作用与维护约束

- 文本位置查询现在带有单次 `ParseChart` 调用内的可变游标状态。它不增加跨调用共享状态；若将来把主扫描并行化，必须先拆分该局部状态。随机逆序查询会走 `O(log LineCount)` 二分，不会退化为旧全表扫描。
- HSpeed Sweep 在存在事件时临时分配 `float[FinalRawTimingCount]`，约为每项 4 字节加数组头。极大谱面可能使该数组进入 LOH；无 HSpeed 事件时返回 `Array.Empty<float>()`，没有该分配。
- Sweep 依赖 `BuildFinalHSpeedEvents` 和 `BuildFinalRawTimingEntries` 已按相同 `GetTimeKey` 规则排序。同时间同 group 的源码最后声明必须继续在 Sweep 前完成去重。
- RawContent trusted path 只允许解析器内部传入已校验 string。未来增加新的规范化规则时，必须更新首次规范化和相应验证，不能让外部输入绕过公开构造函数。
- `SimaiNote.RawContent` 仍独立生成，因为 FixedSoflan、Force Yellow、each 和各类 Note flag 可能使 Note 内容与 Timing 内容不同。

## 文本位置方案

每个生成谱面一行一个 Timing。查询序列模拟 `ParseChart`：每个逗号分别查询 Note 与 Comma 位置，最后查询 EOF；所有查询按源码位置单调不减。

| Timing | 当前线性扫描 | 二分 | 顺序游标 | 二分/当前 | 游标/当前 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 256 | 39.231 us | 4.127 us | 0.640 us | 10.5% | 1.6% |
| 1024 | 450.660 us | 19.068 us | 2.582 us | 4.2% | 0.6% |
| 4096 | 5.779 ms | 242.865 us | 10.250 us | 4.2% | 0.2% |

三种方案均无托管分配。

### 取舍

顺序游标在主解析循环中最合适，复杂度接近 `O(QueryCount + LineCount)`，4096 Timing 时约比当前快 `564x`、比二分快 `23.7x`。当前 `getTextPosition` 的调用都来自向前推进的 `for` 主循环或当前/后续位置，因此正常路径满足单调条件。

二分支持任意顺序查询，代码局部、风险较低，复杂度为 `O(QueryCount * log LineCount)`；即使不采用游标，它在 1024/4096 Timing 上也只需要当前时间的约 `4.2%`。

生产实现采用该混合方案：缓存上次查询 offset、行索引和行首；`offset >= lastOffset` 时推进游标，出现逆序 offset 时对换行表执行 upper-bound 二分并重置游标。这样保留任意错误定位能力，同时让正常解析走最快路径。

## HSpeed 查询方案

### 查询内核上限

本表只测查询算法。二分的 `PreBuiltIndex` 不计建索引成本，`BuildAndLookup` 计入；Sweep 直接消费已排序事件和查询。每个事件对应两个最终 Timing 查询。

| 2048 事件 | 当前全表扫描 | 二分，预建 | 二分，建表+查询 | 顺序 Sweep |
| --- | ---: | ---: | ---: | ---: |
| 1 group | 9.162 ms | 135.442 us | 150.683 us | 30.076 us |
| 8 groups | 5.262 ms | 52.926 us | 78.235 us | 34.610 us |

预建二分查询本身不分配；建表方案分配约 `148-150 KB`，Sweep 只为 group 当前状态分配 `192-352 B`。这张表只说明算法上限，不直接作为生产选择依据。

### 生产最终化形态

公平化基准让三种方案输出相同的 `float[]`，并支付后续并行 Note 阶段的同等 `Parallel.For` 调度：

- 当前：在并行循环中对每个 Timing 扫描全部事件。
- 二分：每次先建 group 索引，再在并行循环中查询。
- Sweep：先顺序生成预计算速度数组，再由并行循环消费；因此比当前额外保留一份 `float[]`。

| 事件 | group | 当前 | 二分 | Sweep | 当前/二分/Sweep 分配 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 128 | 1 | 13.668 us | 9.039 us (`66%`) | 6.649 us (`49%`) | 5.01 / 14.36 / 5.40 KB |
| 128 | 8 | 10.685 us | 9.656 us (`90%`) | 7.179 us (`67%`) | 5.10 / 15.33 / 5.47 KB |
| 512 | 1 | 79.306 us | 20.219 us (`25%`) | 18.787 us (`24%`) | 8.35 / 44.13 / 10.85 KB |
| 512 | 8 | 62.838 us | 19.957 us (`32%`) | 19.017 us (`30%`) | 8.37 / 45.47 / 11.00 KB |
| 2048 | 1 | 937.524 us | 96.729 us (`10%`) | 75.975 us (`8%`) | 20.62 / 164.11 / 34.89 KB |
| 2048 | 8 | 765.901 us | 86.330 us (`11%`) | 74.604 us (`10%`) | 20.51 / 165.55 / 34.94 KB |

### 取舍

Sweep 在所有测试组合中最快。128 事件时也没有出现建表/调度抵消收益的交叉点；2048 事件时相对当前快约 `10.3x-12.3x`。它的分配高于当前，是因为为保留并行 Note 解析多出一个速度数组，但仅为当前的 `1.07x-1.70x`；二分需要 group 索引，为 `2.87x-8.07x`。

生产实现利用现成前提：`BuildFinalHSpeedEvents` 和 `BuildFinalRawTimingEntries` 已按时间排序，同时间同 group 的最后声明也已去重。实现顺序扫描两表，按 group 保存当前速度，写入与 Raw Timing 等长的 `float[]`，随后现有 `Parallel.For` 只负责 Note 解析；无 HSpeed 事件时直接使用 `1f`，不创建速度数组。

如果希望最小化控制流改动，按 group 二分可以直接替换 `GetEffectiveHSpeed` 并保留现有并行结构；代价是更高建表分配，且所有测试规模均慢于 Sweep。

## RawContent 方案

三种管线都保留一个独立的 Note 字符串和相同的最终引用数组：

- 当前：首次 Raw Timing 规范化、HSpeed 回填时再次规范化、最终 Timing 第三次规范化。
- 部分复用：HSpeed 回填保留首次字符串，但最终 Timing 仍重新规范化。
- 完整复用：HSpeed 回填和最终 Timing 都引用首次规范化字符串；Note 仍独立分配。

| Timing | 当前 | 部分复用 | 完整复用 | 当前/部分/完整分配 |
| ---: | ---: | ---: | ---: | ---: |
| 128 | 11.928 us | 8.076 us (`68%`) | 5.347 us (`45%`) | 19.83 / 15.33 / 10.83 KB |
| 1024 | 94.443 us | 67.912 us (`72%`) | 44.540 us (`47%`) | 158.08 / 122.08 / 86.08 KB |
| 4096 | 439.851 us | 260.565 us (`59%`) | 177.345 us (`40%`) | 632.08 / 488.08 / 344.08 KB |

### 取舍

完整复用在全部规模下同时获得最低耗时和分配：4096 Timing 减少 `262.506 us` 与 `288 KB`。字符串不可变，因此 Raw Timing 与最终 Timing 共享已经验证过的 normalized string 不产生所有权风险。

不建议让 `SimaiNote.RawContent` 无条件共享 Timing 字符串。FixedSoflan、Force Yellow 和 Note flag 可能让 Note 层内容与 Timing 层不同；本基准始终保留独立 Note 字符串，结果没有把这部分必要分配算作可消除收益。

生产实现增加了仅供解析器内部使用的“内容已规范化”构造路径：首次执行 lowercase `c`、空白和 FixedSoflan 间距校验，HSpeed 回填只更新数值字段，最终 Timing 直接接收该 string。公开 `SimaiTimingPoint` 构造路径没有放宽校验。

## 实施与验收结果

三个改动均已落地：

1. 文本位置混合游标通过多行 CRLF 位置逐项校验，并重跑完整 `ChartLayoutBenchmarks`。
2. HSpeed Sweep 通过同时间最后声明、0/负速度、自动负 group、fake-each、插值采样、head-only Slide group 和 256 次密集 SV 边界验证，并重跑 `HSpeedDensityBenchmarks`。
3. RawContent 完整复用通过 lowercase `c`、普通/Unicode 空白、FixedSoflan、Force Yellow、each/fake-each 和 Slide 现有验证；Timing/Note 字符串值保持不变。

最终验证为 `22/22`；`MajSimai.csproj` 的 `netstandard2.1`、`net5.0` 至 `net9.0` Release 构建全部通过，Benchmark 项目 Release 构建通过。端到端数据来自生产实现后的独立 ShortRun，不是把隔离内核耗时从旧结果中相减。
