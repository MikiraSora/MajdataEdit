# SimaiParser 性能审计与 Benchmark

状态：已建立可重复 Benchmark，完成 Release/ShortRun 测量与静态代码审计；三个 P1 已于 2026-07-24 按决策落地并复测，P2 与异步正确性问题仍待处理。

## 结论

优化前最值得优先处理的两个 CPU 热点都存在随输入规模放大的复杂度问题：

1. 多行谱面的位置换算对每个 Timing 从头扫描换行表，最坏为 `O(TimingCount * LineCount)`。4096 个 Timing 一行一个时，实测比等价的单行谱面慢 `8.74x`，但分配量只多 `1%`，说明差异主要来自重复扫描。
2. 每个最终 Timing 都会线性扫描完整 HSpeed 事件表，最坏为 `O(TimingCount * HSpeedEventCount)`。每个 Timing 都声明 SV 时，相对普通谱面的耗时比从 128 个 Timing 的 `2.21x` 增长到 2048 个 Timing 的 `4.42x`。

文本位置、HSpeed 最终查询和 RawContent 重复规范化现已修复。元数据的未使用 UTF-8 副本、引用数组池化、无条件并行及异步数组生命周期等 P2/正确性问题不在本次改动范围内。

三个 P1 的候选实现、完整对比结果与推荐取舍见
[SimaiParser P1 候选方案 Benchmark 决策](SimaiParser_P1_Benchmark_Decision.md)。

## Benchmark 项目

项目位于 `MajSimaiX/Benchmark~/MajSimai.Benchmarks.csproj`，目标框架为 `net8.0`，固定使用 BenchmarkDotNet `0.15.8`。

目录名以 `~` 结尾，因此 Unity 不会导入其中的 C# 文件。仓库还提供两层保护：

- `MajSimaiX/.gitignore` 只为 `Benchmark~` 的 `.cs` 和 `.csproj` 源文件解除 `*~` 忽略规则，构建产物仍保持忽略。
- `MajSimaiX/MajSimai.csproj` 显式移除 `Benchmark~/**` 的 `Compile`、`EmbeddedResource` 和 `None` 项，避免 SDK 默认 glob 把 Benchmark 源码编进 MajSimai 库。

基准场景如下：

| 类 | 对比内容 | 参数 |
| --- | --- | --- |
| `ChartLayoutBenchmarks` | 完全等价的紧凑单行谱面和每个 Timing 独占一行的谱面 | 256、1024、4096 Timing |
| `HSpeedDensityBenchmarks` | 普通 Tap 谱面和每个 Timing 都交替声明 `<SV*0.5>`/`<SV*2>` 的谱面 | 128、512、2048 Timing |
| `ParserApiBenchmarks` | `ParseChart`、`ParseChartAsync`、`ParseMetadata`、`Parse` 整文件入口 | 64、1024 Timing |

所有生成输入都会在 `GlobalSetup` 中先解析并校验 Timing 数量，输入构造和校验不计入测量。

在仓库根目录执行：

```powershell
dotnet build 'MajSimaiX\Benchmark~\MajSimai.Benchmarks.csproj' -c Release -p:GeneratePackageOnBuild=false
dotnet run --project 'MajSimaiX\Benchmark~\MajSimai.Benchmarks.csproj' -c Release --no-build -- --job Short --filter '*'
```

只复测一个问题时可使用类名过滤：

```powershell
dotnet run --project 'MajSimaiX\Benchmark~\MajSimai.Benchmarks.csproj' -c Release --no-build -- --job Short --filter '*ChartLayoutBenchmarks*'
dotnet run --project 'MajSimaiX\Benchmark~\MajSimai.Benchmarks.csproj' -c Release --no-build -- --job Short --filter '*HSpeedDensityBenchmarks*'
```

结果默认写入当前目录的 `BenchmarkDotNet.Artifacts/results/`。该目录已由仓库 `.gitignore` 忽略。

## 测量环境

- 日期：2026-07-24
- 系统：Windows 11 24H2
- CPU：AMD Ryzen 7 5800X，8 核 16 线程
- Runtime：.NET 8.0.27，X64 RyuJIT x86-64-v3
- BenchmarkDotNet：0.15.8
- 作业：`ShortRun`，1 次进程启动、3 次预热、3 次正式迭代
- GC：Concurrent Workstation

`ShortRun` 适合确认数量级和缩放趋势，不应把这里的绝对微秒数作为跨机器门槛。优化提交在最终合并前应去掉 `--job Short` 运行默认作业，并同时执行功能验证。

## 优化前基线

### 多行布局

| Timing | Compact | OneTimingPerLine | 耗时比 | Compact 分配 | 多行分配 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 256 | 117.0 us | 176.2 us | 1.51x | 202.41 KB | 205.05 KB |
| 1024 | 383.9 us | 1.252 ms | 3.26x | 797.50 KB | 807.57 KB |
| 4096 | 1.698 ms | 14.841 ms | 8.74x | 3174.85 KB | 3215.26 KB |

换行带来的托管分配只增加约 `1%`，但耗时比随 Timing 数量持续扩大。这与位置换算的二次复杂度一致。

### HSpeed/SV 密度

| Timing | Plain | 每 Timing 一次 SV | 耗时比 | Plain 分配 | SV 分配 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 128 | 70.18 us | 154.93 us | 2.21x | 103.09 KB | 195.70 KB |
| 512 | 200.22 us | 564.69 us | 2.82x | 400.94 KB | 790.10 KB |
| 2048 | 832.36 us | 3.678 ms | 4.42x | 1590.04 KB | 3200.77 KB |

SV 场景按协议额外生成空 HSpeed Timing，因此约 `2x` 的输出对象和分配量有一部分是预期成本；但耗时比从 `2.21x` 增长到 `4.42x` 不能仅由固定的双倍输出解释，符合线性事件表被重复扫描的代码路径。

### API 入口

| Timing | ParseChart | ParseChartAsync | ParseMetadata | ParseWholeFile |
| ---: | ---: | ---: | ---: | ---: |
| 64 | 36.927 us | 44.968 us (`1.22x`) | 1.325 us | 43.546 us (`1.18x`) |
| 1024 | 360.959 us | 384.681 us (`1.07x`) | 8.282 us | 372.152 us (`1.03x`) |

括号内以同规模 `ParseChart` 为基线。CPU 热身后，异步和整文件入口的固定调度成本主要影响小谱面；规模增大后解析本身占主导。这里的 maidata 只有一个非空难度，所以不能外推到七个大谱面同时解析的嵌套并行情形。

## P1 落地后复测

完整方案、候选对比和副作用见 [P1 Benchmark 决策](SimaiParser_P1_Benchmark_Decision.md)。本表使用相同机器、Runtime 和 ShortRun 配置重新运行生产解析器。

| 场景 | 最大规模优化前 | 最大规模优化后 | 结果 |
| --- | ---: | ---: | ---: |
| 4096 Timing，Compact | 1.698 ms | 1.600 ms | -5.8% |
| 4096 Timing，一 Timing 一行 | 14.841 ms | 1.675 ms | -88.7% |
| 多行/Compact | 8.74x | 1.05x | 二次缩放消失 |
| 2048 Timing，Plain | 832.36 us | 766.55 us | -7.9% |
| 2048 Timing，每 Timing 一次 SV | 3.678 ms | 2.095 ms | -43.0% |
| SV/Plain | 4.42x | 2.73x | 事件重复扫描已移除 |

4096 Timing Compact 分配从 `3174.85 KB` 降至 `2983.00 KB`，对应短 RawContent 少创建两份重复字符串。2048 Timing 每槽 SV 的净分配从 `3200.77 KB` 降至 `3120.22 KB`；该净值已经包含 Sweep 临时 `float[]` 的成本。

## 问题明细

### P1（已解决）：文本位置换算为二次扫描

优化前 [`SimaiParser.cs`](../MajSimaiX/Runtime/SimaiParser.cs#L634) 在每次查询时从换行表开头扫描。现实现维护顺序游标，并在 offset 逆序时执行 upper-bound 二分回退；换行表和既有行列定义保持不变。

已评估以下方案：

- 解析主循环按顺序维护当前行号和行首偏移，直接把已知位置写入 Timing。
- 对任意偏移查询使用二分查找，把单次查询从 `O(LineCount)` 降为 `O(log LineCount)`。

最终采用混合游标；`OneTimingPerLine / Compact` 在 4096 Timing 时由 `8.74x` 降至 `1.05x`，新增多行 CRLF 验证逐项对照旧线性算法的行列结果。

### P1（已解决）：HSpeed 查询为 Timing 乘事件数

优化前最终化阶段对每个 Raw Timing 调用 `GetEffectiveHSpeed` 并扫描完整事件表。现实现对排序、去重后的事件和 Raw Timing 顺序 Sweep，生成速度数组后继续使用原有并行 Note 解析；插值段起点仍使用原查询函数，不改变插值生成语义。

候选方案包括：

- 每组二分查找最后一个 `Timing <= target` 的事件，复杂度为 `O(T * log H_group)`；或
- 同时顺序扫描已排序 Raw Timing 和事件，为每个 group 维护游标，复杂度接近 `O(T + H)`。

最终采用 Sweep。同一时间、同一 group 的“源码顺序最后者生效”仍在 Sweep 前按 `Order` 去重；无 HSpeed 事件走 `1f` fast path，不分配速度数组。

### P1（已解决）：RawContent 重复规范化与复制

优化前每个普通 Timing 至少经过以下重复工作：

1. `addRawTiming` 构造 `SimaiRawTimingPoint`，在 [`SimaiRawTimingPoint.cs`](../MajSimaiX/Runtime/SimaiRawTimingPoint.cs#L21) 过滤字符并创建字符串。
2. 回填最终 HSpeed 时在 [`SimaiParser.cs`](../MajSimaiX/Runtime/SimaiParser.cs#L1633) 从已经规范化的 `RawContent` 再构造一次 `SimaiRawTimingPoint`。
3. `SimaiRawTimingPoint.Parse` 构造 `SimaiTimingPoint`；[`SimaiTimingPoint.cs`](../MajSimaiX/Runtime/SimaiTimingPoint.cs#L31) 再扫描并再次创建等价字符串。
4. 单 Note 解析还会为 `SimaiNote.RawContent` 创建字符串。

普通紧凑谱面实测约分配 `0.78 KB/Timing`：1024 Timing 为 `797.50 KB`，4096 Timing 为 `3174.85 KB`。其中包含最终对象模型的必要分配，不能全部消除，但重复字符串扫描和副本没有必要。

现实现让 HSpeed 回填保留首次规范化 string，并由 internal trusted path 交给最终 Timing；公开构造函数仍执行原有规范化。lowercase `c`、普通/Unicode 空白、FixedSoflan `@`、Force Yellow 与 Note/Timing RawContent 值均已纳入 22 项验证。

### P2：元数据解析包含无效工作和栈风险

[`SimaiParser.cs`](../MajSimaiX/Runtime/SimaiParser.cs#L434) 对完整输入执行 UTF-8 `GetByteCount`、分配 `byte[]` 并调用 `GetBytes`，随后从未读取该数组。`hash` 已由调用方传入，所以这段工作可直接删除。

同一方法还先统计全部换行，再用 `stackalloc Range[lineCount]` 保存整份文件的行范围。它会额外完整扫描一次输入，而且栈使用量与可控输入行数线性增长；大型逐行谱面存在 `StackOverflowException` 风险。建议改为单遍行枚举，或仅对小数量使用 `stackalloc`、超过阈值后租用数组。

### P2：无条件并行和 Task.Run 包装

- `Parse(SimaiMetadata)` 总是 `Parallel.For(0, 7)`，包括空难度。
- `ParseChart` 总是对最终 Timing 使用 `Parallel.For`。
- `ParseChartAsync` 和 `ParseMetadataAsync` 本质上用 `Task.Run` 包装 CPU 工作。

实测表明调度成本在 64 Timing 上为同步图表解析的 `22%`，到 1024 Timing 降为 `7%`。建议为 Timing 数量设置顺序/并行阈值，只调度非空难度，并避免“难度并行”内部再次无条件“Timing 并行”。异步 API 是否负责切线程属于产品语义，应明确后再改；不能只为了方法名为 Async 而叠加线程池任务。

### P2：小热路径和池化策略

- [`SimaiNoteParser.cs`](../MajSimaiX/Runtime/SimaiNoteParser.cs#L55) 即使没有 `/` 也会租用 `Range[]`、执行 Split，并在归还时清空整个数组。普通单 Note 应直接走无池化 fast path。
- [`ForceYellowNormalizer.cs`](../MajSimaiX/Runtime/ForceYellowNormalizer.cs#L13) 对每个图表执行 LINQ `Where + OrderBy`，即使没有任何 Force Yellow；输入 Timing 在此前已经按时间排序，可改为单遍扫描并提供无 Yellow 快速返回。
- `BuildFinalHSpeedEvents` 在去重前后各排序一次。建立按 group 的最终索引后应只保留一次必要排序。
- `ArrayPool<string>`、`ArrayPool<SimaiChart>`、`ArrayPool<Task<SimaiChart>>` 等引用数组归还时没有 `clearArray: true`，可能让完整 fumen、Chart 和 Task 对象图继续被共享池引用。固定长度 7/16 的临时数组应评估直接分配；继续池化时必须清空引用槽位。

这些项目需分别用基准或分配剖析验证，不能仅凭静态代码一次性重写。

## 异步入口的正确性风险

这不是 Benchmark 得出的性能结论，但在审计中发现应优先修复：[`ParseAsync(string, hash)`](../MajSimaiX/Runtime/SimaiParser.cs#L102) 把字符串复制到租用的 `char[]`，返回尚未完成的 Task 后立即在 `finally` 中把数组归还池。后续 `Task.Run` 可能在数组被复用后才读取该 `ReadOnlyMemory<char>`，存在数据竞争和错误解析风险。

最直接的修复是让任务直接持有字符串内存，例如转发 `content.AsMemory()`；或把方法改成 `async` 并在归还数组前 `await` 完整解析。前者还能去掉一次完整复制和池操作。

## 后续建议实施顺序

P1 已完成：文本位置混合游标、HSpeed Sweep、RawContent 完整复用均通过端到端 Benchmark 与 22/22 功能验证。后续顺序为：

1. 先修复 `ParseAsync(string, hash)` 的数组生命周期，并补并发回归测试。
2. 删除元数据的未使用 UTF-8 副本，替换按总行数 `stackalloc` 的实现。
3. 最后调整并行阈值、Note fast path、Force Yellow 排序和引用数组池化；这些改动的收益更依赖谱面分布与运行环境。

每一步都应单独提交并同时运行功能验证与 Benchmark，避免性能重构掩盖 Simai、HS/SV、FixedSoflan 或 Force Yellow 语义回归。
