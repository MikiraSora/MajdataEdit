# Each 时间判定、频谱显示与 MA2 统计讨论纪要

## 1. 背景与目标

本次工作处理以下三个相关问题：

1. 将 `each` 判断从“单个 `SimaiTimingPoint` 内的物件数量”改为“按实际判定时间分组”。
2. 修正频谱视图中 each 物件的显示逻辑。
3. 修正导出 MA2 时 `TTM_EACHPAIRS` 的统计逻辑。

核心原则是：**each 由同一判定时间内的有效头物件组成，与文本邻接、原始 `SimaiTimingPoint` 边界和 HS/Soflan 分组无关。**

## 2. 代码调查结论

### 2.1 原有频谱逻辑

频谱原先在 `MainWindowCore.DrawWave()` 中使用当前 `SimaiTimingPoint.Notes` 判断 each：

```csharp
var isEach = notes.Count(o => !o.IsSlideNoHead) > 1;
```

该做法只能识别同一个 `SimaiTimingPoint` 内的多押，无法识别同一时间但因 HS/Soflan 分组等原因被解析为多个 timing point 的物件。

### 2.2 解析器行为

MajSimaiX 解析器可能在同一时间生成多个 `SimaiTimingPoint`，例如：

- 不同 HS/Soflan 分组的物件；
- 同一时刻的独立解析片段；
- HSpeed 相关事件与物件。

解析器内部使用 `1e-9` 秒作为时间比较容差。Fake Each 使用反引号语法，并会按 128 分音产生真实时间偏移，因此不应被合并为 each。

### 2.3 原有 MA2 逻辑

MA2 导出原先逐个 `SimaiTimingPoint` 统计满足条件的物件数量，并将物件数大于 1 的 timing point 计为一个 `TTM_EACHPAIRS`。

该逻辑存在两个问题：

- 无法跨 `SimaiTimingPoint` 合并同一时间的物件；
- 排除了 Slide 星头，与频谱中的 each 语义不一致。

## 3. 最终确认的 Each 定义

### 3.1 时间分组

- 按物件头部的判定时间 `SimaiTimingPoint.Timing` 分组。
- 时间差不超过 `1e-9` 秒视为同一时间。
- 不真正合并或修改解析器生成的 `SimaiTimingPoint`。
- 仅在 each 分析层创建逻辑时间组，保留各物件原本的 HS 分组、原文位置和显示速度信息。
- Slide 按星头 `Timing` 参与分组，不按 `SlideStartTime` 分组。
- 不同 HS/Soflan 分组但时间相同的物件属于同一个 each。
- Fake Each 因实际时间不同，不属于同一个 each。

### 3.2 参与 each 的物件

以下物件可作为 each 成员：

- Tap；
- Hold；
- Touch；
- TouchHold；
- Slide 星头；
- Force Star，例如 `1$`；
- Break 和 EX 等带修饰标记的上述物件。

以下物件不参与 each：

- Mine，即带 `m` 的物件；
- Mine Slide；
- 无头 Slide 路径；
- Fake Each 中时间已经错开的物件。

Mine 不仅不计入 each 成员数量，也不会因为同一时间存在其他 each 成员而继承 each 样式。

例如：

- `1/2m`：有效成员只有 `1`，不构成 each；
- `1/2/3m`：`1` 和 `2` 构成 each，`3m` 不属于 each；
- `1/2-4m[4:1]`：Mine Slide 不参与，因此不构成 each。

### 3.3 成组与计数

- 同一时间至少存在两个有效头物件时，构成一个 each 时间组。
- 按物件数量判断，不按唯一位置数量判断。
- 同一位置存在两个有效物件时仍可构成 each；重复物件是否合法由现有语法检查或无理检查负责。
- 三押及更多物件仍只记一个 each 时间组。
- `TTM_EACHPAIRS` 统计的是 each 时间组数量，不是两两组合数量，也不是 each 成员总数。

例如 `1/2/3` 的 `TTM_EACHPAIRS` 为 `1`，不是 `3`。

## 4. 频谱显示规则

### 4.1 通用颜色优先级

关闭“按 HSpeed 组着色”时：

- 普通 each 头物件使用金色；
- Break 物件优先使用橙红色；
- Mine 使用自身的非 each 样式；
- 非 each 物件保持原有类型颜色。

启用“按 HSpeed 组着色”时：

- HSpeed 分组颜色优先于 each、Break 等类型颜色；
- 不再额外把 each 改为金色，以免丢失分组信息。

### 4.2 Tap 与 Force Star

- 普通 each Tap 使用金色。
- Force Star 作为 Tap 头物件参与 each，并在属于 each 时使用金色。
- Break Tap 或 Break Star 保持橙红色。

### 4.3 Hold

- 普通 Hold 属于 each 时，整条 Hold 持续线保持金色。
- Break Hold 保持橙红色优先。

### 4.4 Touch

- 普通 Touch 属于 each 时使用金色头标记。
- Break Touch 使用橙红色头标记。

### 4.5 TouchHold

- TouchHold 的持续线继续保留原有多段彩色样式。
- TouchHold 属于 each 时，在起点增加金色头标记。
- Break TouchHold 在起点增加橙红色头标记。
- HSpeed 分组着色启用时，头标记使用 HSpeed 分组颜色。

### 4.6 Slide

- Slide 星头作为有效头物件参与 each。
- Slide 星头属于 each 时使用金色；Break Slide 星头保持橙红色。
- Slide 虚线路径不体现 each 状态。
- 普通 Slide 路径统一使用蓝色。
- Break Slide 路径使用橙红色。
- 移除原先“同一 timing point 内有两条 Slide 时路径染金”的旧逻辑。
- 同头分叉 Slide 也只由星头体现 each，路径分别使用普通或 Break Slide 颜色。

## 5. MA2 统计范围

本次仅修正与 each 直接相关的：

```text
TTM_EACHPAIRS
```

以下统计保持原样，不扩大修改范围：

- `T_REC_*`；
- `T_NUM_*`；
- `T_JUDGE_*`；
- 分数与达成率统计。

## 6. 验收矩阵

| 场景 | 频谱结果 | `TTM_EACHPAIRS` |
|---|---|---:|
| `1/2/3` | 三者使用 each 样式 | 1 |
| `1/2m` | 两者均不使用 each 样式 | 0 |
| `1/2/3m` | `1/2` 使用 each 样式，Mine 不使用 | 1 |
| Tap + Slide | Tap 与 Slide 星头使用 each 样式，路径为蓝色 | 1 |
| 两条 Slide | 星头使用 each 样式，路径分别按普通/Break 色显示 | 1 |
| Tap + TouchHold | TouchHold 增加 each 头标记，持续线不变 | 1 |
| Break + 普通物件 | Break 保持橙红色，普通物件使用 each 样式 | 1 |
| 跨 HS 组但时间相同 | 合并为 each；启用组着色时 HSpeed 颜色优先 | 1 |
| Fake Each | 实际时间不同，不合并 | 0 |
| Mine Slide 与普通物件 | Mine Slide 不参与 | 0 |
| 无头 Slide 与普通物件 | 无头 Slide 不参与 | 0 |
| Force Star 与普通物件 | Force Star 参与 each | 1 |

## 7. 实现结果

### 7.1 `EachNoteAnalysis.cs`

新增共享 each 分析器：

- 按时间排序并使用 `1e-9` 秒容差归组；
- 筛选有效 each 候选物件；
- 记录属于 each 的具体 `SimaiNote` 实例；
- 提供 each 时间组总数；
- 供频谱显示与 MA2 导出共同使用，避免两套规则再次产生偏差。

### 7.2 `SimaiProcess.cs`

- 谱面解析后立即生成并缓存 `EachNoteAnalysis`。
- 清空谱面数据时同步重置分析结果。
- 频谱反复重绘时只需查询缓存，不必重复遍历整张谱面。

### 7.3 `MainWindowCore.cs`

- each 判断由 timing point 级别改为逐物件查询。
- 补齐 Touch/TouchHold 的 Break 与 each 头标记。
- Slide 星头与路径分别着色。
- 移除 Slide 路径隐式 each 着色。
- 保留 HSpeed 分组着色优先级。

### 7.4 `Ma2Export/SimaiChartConverter.cs`

- `TTM_EACHPAIRS` 改为使用共享分析器的 `GroupCount`。
- 同一时间跨多个 `SimaiTimingPoint` 的物件能够正确合并统计。
- Slide 星头、Mine、无头 Slide 等规则与频谱保持一致。

### 7.5 HSpeed 频谱显示设置

同一提交还包含此前未提交的 HSpeed 频谱显示开关接线：

- 在主窗口中显示并保存 `DrawHSpeedChanges` 设置；
- 设置读取时同步复选框状态；
- 设置变化后立即重绘频谱。

## 8. 验证结果

执行了 Release 构建：

```powershell
dotnet build MajdataEdit.sln -c Release --no-restore
```

结果：

- 构建成功；
- 0 个错误；
- 仅存在项目原有的目标框架生命周期和可空引用警告。

另外使用临时验证程序直接调用 MajSimaiX 解析器、共享 each 分析器和 MA2 转换器，验证了 12 组谱面输入以及时间容差和成员归属规则：

- 三押；
- Mine 混合；
- Mine Slide；
- Tap + Slide；
- 双 Slide；
- TouchHold；
- Break；
- Force Star；
- 跨 HS 分组；
- Fake Each；
- 无头 Slide；
- `1e-9` 时间容差边界。

全部断言通过。

## 9. 提交信息

相关代码已提交：

```text
9ec7b3d Fix time-based each handling and spectrum controls
```

该提交尚未推送远端。
