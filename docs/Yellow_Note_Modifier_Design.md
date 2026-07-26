# simai `y` Yellow 物件修饰符设计

状态：规格、MajSimaiX/MajdataEdit 实现、MajdataView 运行时接入与验证已完成。本文保留 `grill-me` 访谈决策链；“最终决策”与“已确认”条目构成本阶段实现合同，被取代的早期建议仅用于解释取舍。

## 目标

为 MajSimaiX 增加逐物件的小写 `y` 修饰符。它与 `b`、`x` 一样附着于单个 simai note token，用于让目标物件显式呈现 EACH 使用的黄色外观，即使同一实际 timing 只有这一个物件。

## 已确认需求

1. 修饰符使用精确的小写字符 `y`，含义命名为 Yellow。
2. `y` 是逐物件修饰符，不是 timing 级命令。
3. `y` 的直接视觉目标是 EACH 物件使用的黄色外观。
4. `y` 与 Break 修饰符 `b` 互斥；二者不能在不合法组合中被静默降级或按源码顺序覆盖。
5. 设计决策通过逐题访谈完成，每次只处理一个问题。
6. `y` 是纯外观标志：不改变逻辑 EACH、判定、得分、音效或不可理检查。

## 术语

- **自然 EACH**：同一实际 timing 至少存在两个有效物件，由当前 `EachNoteAnalysis` 自动推导。
- **Force Yellow（显式 Yellow）**：单个 note token 含 `y`，由物件自身携带的标志决定黄色外观。
- **逻辑 EACH**：会影响 EACH 分组、`TTM_EACHPAIRS`、EACH 连线或其他玩法语义的同押关系。
- **黄色外观**：当前编辑器时间轴中自然 EACH 使用的 `Color.Gold`，以及预览运行时中对应的 EACH 物件素材/配色。

## 实现结果

### 解析和模型

- `ForceYellowModifierParser` 在既有 flag/结构解析前识别并移除 `y`，以组件作用域设置 `IsForceYellow` 和 `ForceYellowSlideSegmentIndices`；`RawContent` 不含 `y`，`SimaiChart.Fumen` 保留原文。
- `SimaiChart` 在实际 timing 确定后执行自然 EACH 规范化，规则与编辑器 `EachNoteAnalysis` 一致，只清除冗余的头部 `IsForceYellow`。
- 非法位置、重复 `y`、大写 `Y`、`b/y` 和 `m/y` 冲突均抛出 `InvalidSimaiSyntaxException`；MajdataEdit 的独立 `SyntaxCheck` 复用同一组件解析规则。
- managed `SimaiNote` 已公开两个 Force Yellow 属性；旧 JSON 缺少字段时得到 `false` 和空数组。
- 旧 `MajSimai_Parse` native ABI 未增加字段，x64 `UnmanagedSimaiNote` 仍为 64 字节且既有偏移保持不变。
- MajdataView 已消费 managed JSON 中的两个 Force Yellow 属性；自然 EACH 的逻辑状态与显式 Yellow 外观状态保持分离。

### 语法检查

- `SyntaxModule/SyntaxCheck.cs` 使用独立于 MajSimaiX 的规则校验 Tap、Hold、Slide、Touch 和 TouchHold。
- `SyntaxCheck` 在 FixedSoflan 处理后调用 Force Yellow 解析器，并按 `*` 分支分别检查，因而与 MajSimaiX 对已覆盖的接受/拒绝矩阵一致。
- Slide 结构检查会在 Force Yellow 冲突检查完成后移除既有轨迹 `b` 标志，避免合法的不同 `*` 分支 Break 被旧结构规则误报。

### EACH 推导和编辑器渲染

- `EachNoteAnalysis` 按实际 timing 聚合候选物件，候选数大于 1 时才把整组记为自然 EACH。
- `MainWindowCore.DrawWave` 现在把 `EachAnalysis.Contains(note)` 与 `note.IsForceYellow` 合并为 `isEach`，然后把 Tap、Hold、Touch、TouchHold 头部和 Slide 星头绘制为 `Color.Gold`。
- 新属性在渲染入口合并为 `isEachAppearance = naturalEach || note.IsForceYellow`；`IsForceYellow` 未传入 `EachNoteAnalysis`，不会改变分组结果。
- Slide 轨迹按 `RawContent` 拆段并读取逐段索引，Force Yellow 使用 `Color.Gold`；HSpeed group 诊断色仍按自然 EACH 的既有优先级覆盖它。

### MajdataView 运行时

- 普通 Tap、Force Star、Hold、Touch、TouchHold 与 Slide/Wifi 静态星头使用 `naturalEach || isForceYellow` 选择 EACH 素材；`isEach` 本身、Touch 分组和 `EachLineDrop` 逻辑不变。
- Slide/Wifi 分别保存“移动星 Yellow”和“轨迹 Yellow”状态。`IsForceYellow` 决定静态星头以及同一连接 Slide 分支的全部移动星；`ForceYellowSlideSegmentIndices` 只决定对应轨迹段，不会单独染黄移动星。
- 无头 Slide 隐藏的静态星头带 `y` 时，仍由 `IsForceYellow` 让可见移动星变黄；同头 `*` 拆出的分支各自读取自己的标志，不跨分支传播。
- JSON 加载在实例化任何物件前严格校验逐段索引：非 Slide 不得携带索引，索引必须非负、严格递增、无重复且不越界；字段缺失或显式 `null` 继续按空数组兼容。

### 其他消费方

- 音效生成、不可理检查和计分目前不根据自然 EACH 外观分支；若 `y` 定义为纯外观，这些模块原则上无需改变行为。
- 镜像逻辑只改写位置和 Slide 方向，未知普通字符会原样保留；语法层接受 `y` 后，镜像预计无需专门转换。
- MA2 导出器以私有 `!y` 保存当前记录自身的 Yellow，以分支级 `!yh` 保存 Slide/Wifi 移动星 Yellow；同一首段可规范输出 `!yh!y`。这是独立的导出保真协议；当前 MajdataView 只消费 managed JSON，不读取 MA2。未实现扩展的外部读取器行为不在兼容合同内。
- MA2 的 `TTM_EACHPAIRS` 仍只由自然 EACH 分组数生成，不读取 Force Yellow 属性。

## 实际实现文件

| 模块 | 已实现职责 |
| --- | --- |
| `MajSimaiX/Runtime/SimaiNote.cs` | 新增 `IsForceYellow` 与 `ForceYellowSlideSegmentIndices` 数据属性 |
| `MajSimaiX/Runtime/SimaiNoteParser.cs` | 识别并移除 `y`、设置属性、拒绝互斥组合 |
| `MajSimaiX/Runtime/ForceYellowModifierParser.cs` | 组件级解析、清理、冲突和逐段索引 |
| `MajSimaiX/Runtime/ForceYellowNormalizer.cs` | 清除自然 EACH 中冗余的头部标志 |
| `SyntaxModule/SyntaxCheck.cs` | 接受合法 `y` 并拒绝所有 `b`/`y` 冲突 |
| `MainWindowCore.cs` | 合并自然 EACH 与 Force Yellow 的时间轴外观 |
| `SlideSegmentTimingAnalysis.cs`、`ForceYellowSlideSegmentHelper.cs` | 时间轴与导出共享逐段作用域校验 |
| `MajdataView/Assets/Scripts/ForceYellowAppearance.cs` | 独立计算 EACH/Force Yellow 外观、连接 Slide 移动星传播和逐段轨迹映射，并提供严格索引校验 |
| `MajdataView/Assets/Scripts/JsonDataLoader.cs` | 在实例化前校验 managed JSON，并把头部、移动星和逐段轨迹状态传播到运行时组件 |
| `MajdataView/Assets/Scripts/Notes/*.cs` | 在不改变逻辑 `isEach` 的前提下，为 Tap/Hold/Touch/TouchHold/Slide/Wifi 选择 EACH 素材 |
| `Ma2Export/SimaiChartConverter.cs` | 以 `!y` 保存当前记录状态，并在每个 Yellow Slide/Wifi 分支首段以 `!yh` 保存移动星状态 |
| 文档与验证器 | 记录语法矩阵并覆盖合法、非法、JSON、MA2 和 ABI 回归 |

## 决策记录

| 编号 | 决策 | 状态 |
| --- | --- | --- |
| D-001 | 使用逐物件小写 `y` 表示 Force Yellow | 已确认 |
| D-002 | `y` 与 `b` 互斥 | 已确认 |
| D-003 | `y` 仅改变物件外观，不属于逻辑 EACH | 已确认 |
| D-004 | 支持全部常规物件及无头 Slide；Slide/Wifi 的星头与轨迹独立修饰，连接 Slide 的轨迹 `y` 逐段生效 | 已确认 |
| D-005 | 允许 `y+x`、`y+$`/`$$`、`y+@`；按 Slide 分支禁止 `y+b`/`y+m`；自然 EACH 中丢弃 `y`；`y` 与自然 EACH 共享 HSpeed 配色优先级 | 已确认 |
| D-006 | 轨迹接受两种位置；重复 `y` 非法；仅小写 `y`；兼容 header 标志顺序无关；`y` 专属非法形式抛出语法异常 | 已确认 |
| D-007 | 使用私有 `!y` 保存当前记录 Yellow，并以分支级 `!yh` 保存 Slide/Wifi 移动星 Yellow；不承诺未实现该扩展的读取器行为 | 已确认并扩展 |
| D-008 | MajdataView 已按独立外观状态接入；头部 `y` 控制静态星头和同分支移动星，逐段索引只控制轨迹，`*` 分支互不传播 | 已确认并完成 |
| D-009 | 公共属性采用 Force Yellow 命名；本阶段保持旧 native ABI 不变且仅 managed API 完整暴露；序列化、测试和版本要求 | 已确认 |

## 访谈记录

### Q1：`y` 是否只改变外观？

状态：已回答，采用推荐方案。

最终决策：`y` 是**纯物件外观标志**。它让该物件选择自然 EACH 的黄色素材/配色，但不把它计入逻辑 EACH，不凭空生成 EACH 连线，不增加 `TTM_EACHPAIRS`，也不改变判定、得分、音效或不可理检查结果。这样“单个物件”仍然保持单物件语义，MA2 统计也不会被伪造。

### Q2：`y` 应支持哪些物件和可见部分？

状态：已回答，采用推荐物件范围，但用户明确要求无头 Slide 也支持 `y`。

推荐答案：与当前自然 EACH 的黄色外观覆盖面完全一致：

- Tap，包括普通 Tap 和 `$`/`$$` Force Star；
- Hold，包括头部和 Hold 条；
- Touch；
- TouchHold 的头部，主体继续沿用现有 TouchHold 外观；
- Slide/Wifi 的**可见星头**，Slide/Wifi 轨迹不变。

据此，无头 Slide `!`/`?` 没有可染黄的星头，应拒绝 `y`，而不是接受一个无视觉效果的标志。建议的典型合法形式包括 `1y`、`1xy`、`1y$`、`1yh[4:1]`、`B1y`、`B1yh[4:1]` 和 `1y-3[8:1]`。

澄清：当前不存在“无头 Slide 轨迹的 EACH 外观”。`EachNoteAnalysis.IsCandidate` 明确排除 `IsSlideNoHead`；编辑器绘制普通 Slide 时，也只有可见星头调用接受 `isEach` 的 `GetTapStarColor`，轨迹始终调用不接收 EACH 状态的 `GetSlideColor`。因此，即使普通 Slide 属于自然 EACH，其轨迹也不会变黄。若允许 `y` 改变无头 Slide 轨迹，就需要定义一种全新的 Yellow Slide 轨迹视觉，不再属于“复用 EACH 外观”的当前目标。

最终范围决策：Tap、`$`/`$$` Force Star、Hold、Touch、TouchHold、Slide/Wifi 均支持 `y`；无头 Slide `!`/`?` 也必须接受并保存 `y`。无头 Slide 没有可见的静态星头，但 MajdataView 的移动星仍继承头部 `IsForceYellow` 并显示 Yellow；轨迹必须自行带 `y` 才显示 Yellow。

### Q3：无头 Slide 的 `y` 应把什么染黄？

状态：原结论已被 Q9 后的用户澄清以及后续 MajdataView 接入决策取代。

被取代的结论：曾约定单个 `y` 会把整个 Slide/Wifi 染黄。最新规则改为按组件位置修饰：星头位置的 `y` 不扩散到轨迹，轨迹位置必须自行带 `y`。

最终补充：星头位置的 `y` 同时决定该 Slide 分支的移动星外观。对无头 Slide，静态星头虽不可见，移动星仍可见并显示 Yellow；连接 Slide 的该状态贯穿同一分支的全部移动星，但不传播到 `*` 拆出的其他分支。

### Q4：`y` 是否允许与 EX/Critical 修饰符 `x` 共存？

状态：已回答，采用推荐方案。

最终决策：允许，并且修饰符顺序不改变语义。`1yx` 与 `1xy` 都表示同时具有 EX/Critical 行为和 Yellow 外观的 Tap；同一组合原则适用于支持 `x` 的 Hold、Touch、TouchHold 和 Slide 星头。`x` 保留既有判定/效果语义，`y` 只选择 Yellow 视觉，两者职责没有冲突。`b`/`y` 互斥规则不因此放宽。

### Q5：`y` 是否允许与 Mine 修饰符 `m` 共存？

状态：已回答，采用推荐方案。

最终决策：不允许，`y` 与 `m` 互斥。当前 Mine 的灰色外观优先于 Break、自然 EACH 和 HSpeed group 配色；Yellow 则要求同一可见部分使用黄色。若二者共存，要么 Mine 灰色覆盖 `y` 而使合法语法无效果，要么 Yellow 覆盖灰色而隐藏 Mine 身份。解析器和语法检查器必须拒绝 `ym`、`my` 及 Slide 头部/轨迹上的对应组合，不得按字符顺序或颜色优先级消解冲突。

在 Q13 确认组件/分支边界后，`m/y` 采用同样的 Slide 分支级互斥：同一 Slide 分支的星头或任一连接段出现 `m` 后，该分支任何位置都不得出现 `y`；`/` 分开的物件和 `*` 拆出的其他分支分别检查。原因是当前 `IsMineSlide` 与 `IsMine` 一样会使该 `SimaiNote` 的星头进入 Mine 灰色外观。

### Q6：`y` 是否允许与 FixedSoflan `@` 共存？

状态：已回答，采用推荐方案。

最终决策：允许，但完全沿用 `@` 的既有位置规则，`y` 必须属于 `@` 之前的基础 note：

- `1y@`、`1y@600`：合法 Yellow + FixedSoflan Tap；
- `1y@-3[8:1]`、`1y@600-3[8:1]`：合法 Yellow + FixedSoflan Slide；
- `1@y`、`1@y600`：非法，因为 `@` 之后只能是速度值或 token 结束；
- 无头 Slide 即使允许 `y`，仍不得使用 `@`，因为 FixedSoflan 目前只支持可见 Slide 星头。

两项功能互不改变彼此语义：`y` 选择 Yellow 外观，`@` 固定物件视觉速度。

### Q7：自然 EACH 中是否允许显式 `y`？

状态：已回答，选择解析时丢弃标志。

原推荐答案（未采用）：允许，把它视为幂等的显式外观声明。例如 `1y/2` 中两个物件仍按原规则构成一个自然 EACH；`1` 额外保存 `IsForceYellow = true`，`2` 没有 Force Yellow 标志，但两者当前都显示黄色。`y` 不增加物件数、不重复计算 EACH、不生成额外连线，也不改变 `TTM_EACHPAIRS`。保留这个冗余标记的价值是：以后若把 `1` 移出同押，它仍保持 Force Yellow 外观。

最终决策：物件一旦属于自然 EACH，解析模型必须丢弃该物件源码中的 Force Yellow 状态，令 `IsForceYellow = false`。例如 `1y/2` 的 `1` 在最终 `SimaiChart`/JSON 中不再携带 Force Yellow 标志；它只因自然 EACH 显示黄色。`y` 不增加物件数、不重复计算 EACH、不生成额外连线，也不改变 `TTM_EACHPAIRS`。

原始 maidata 文本仍由编辑器原样保存，因此文本中的 `y` 不会被自动改写或删除；“丢弃”只针对解析后的语义标志。若用户随后从源码中删除同押伙伴并重新解析，仍可由保留在源码中的 `y` 恢复 Yellow 状态。

### Q8：用什么规则判定需要丢弃 `y` 的自然 EACH？

状态：已回答，采用推荐方案。

最终决策：完全复用当前 `EachNoteAnalysis` 的语义，而不是只检查源码中是否出现 `/`。即在同一实际 timing（容差 `1e-9`）存在至少两个有效候选物件时，整组属于自然 EACH，并清除组内所有 `IsForceYellow`。这会统一覆盖 `/` 多押、双数字简写，以及由不同 timing point 最终落在同一实际时刻的物件；Mine、Mine Slide 和无头 Slide 继续按现有规则不参与自然 EACH 候选计数。这样“是否丢弃”与编辑器实际显示的自然 EACH 保持一致，不会出现同样的同押因写法不同而得到不同 Force Yellow 标志。

实现约束：自然 EACH 判定不能只留在 `MainWindowCore` 的绘制路径。MajSimaiX 最终输出 `SimaiChart`/JSON 前必须执行与编辑器一致的组级规范化，或者由两者调用同一个共享分析实现，避免下游看到尚未清除的 `IsForceYellow`。

### Q9：自然 EACH 中的 Yellow Slide 是否恢复普通轨迹？

状态：用户给出更精确的组件级规则，原问题中的无头 Slide 推导已被纠正。

由 Q7 和 Q8 可推导：`1y-3[8:1]/2` 中的有头 Slide 属于自然 EACH，所以解析后清除 `IsForceYellow`。它的星头仍因自然 EACH 显示黄色；轨迹从未带 `y`，因此保持普通轨迹外观。

最终澄清：

- `1y!-3[8:1]/2` 中的 `y` 修饰隐藏的星头组件；`!` 只隐藏静态星头，MajdataView 中仍可见的移动星继承 `IsForceYellow` 并显示 Yellow。
- 星头位置的 `y` 不扩散到 `-3[8:1]`，所以该轨迹保持普通外观。
- 只有轨迹组件自身带 `y`，例如用户提出的 `-3[8:1]y` 或 `-3y[8:1]`，轨迹才使用 Yellow 外观；最终接受哪些位置仍需后续确认。
- 该规则要求数据模型区分 `IsForceYellow`（普通物件/星头）和 `ForceYellowSlideSegmentIndices`（逐段 Slide Yellow 状态），不能再用一个标志修饰整个 Slide。

### Q10：连接 Slide 的轨迹 `y` 是逐段生效还是整条生效？

状态：已回答，采用推荐方案。

以 `1-3y[8:1]-5[8:1]` 为例：

最终决策：只把 `1→3` 这一段染黄，`3→5` 保持普通。`1-3y[8:1]-5y[8:1]` 才会让两段都变黄。该规则同样适用于任意长度的连接 Slide；每个 `y` 只能影响其所在的一个轨迹段。

实现后果：不能只新增一个轨迹布尔值，也不能沿用当前 `IsSlideBreak`/`IsMineSlide` 向全部拆分段扩散的模型。解析结果必须保存逐段 Yellow 信息，并让 JSON、编辑器轨迹绘制、MA2 拆段和 MajdataView 连接 Slide 拆段分别读取对应段的状态。

### Q11：同一轨迹段允许在哪些位置书写 `y`？

状态：已回答，采用推荐方案。

最终决策：同时接受以下两种等价形式，并在文档示例中以时长前形式作为规范写法：

- `1-3y[8:1]`：`y` 位于终点后、时长前，推荐的规范形式；
- `1-3[8:1]y`：`y` 位于本段完整时长后，兼容形式。

在连接 Slide 中，后置形式仍绑定前一段，例如 `1-3[8:1]y-5[8:1]` 只标记 `1→3`。不接受 `-y3[8:1]` 等无法明确归属终点的写法。

### Q12：同一组件重复书写 `y` 是否报错？

状态：已回答，采用推荐方案。

最终决策：报语法错误，不把重复标志静默归一化。具体边界如下：

- `1yy`：非法，同一 Tap/星头重复 `y`；
- `1-3yy[8:1]`：非法，同一轨迹段重复 `y`；
- `1-3y[8:1]y`：非法，同一段在时长前后各写一次；
- `1y-3y[8:1]`：合法，前一个 `y` 修饰星头，后一个修饰轨迹；
- `1-3y[8:1]-5y[8:1]`：合法，两个 `y` 分别修饰不同轨迹段。

这样可以尽早暴露误写，同时保留组件级组合能力。

### Q13：`b` 与 `y` 的互斥边界是什么？

状态：已回答，采用修正后的推荐方案。

代码事实：轨迹位置的 `b` 会设置 `IsSlideBreak`，现有时间轴的星头配色同时检查 `IsBreak` 和 `IsSlideBreak`。因此 `1y-3b[8:1]` 虽然把两个修饰符写在不同位置，仍会让同一个星头同时要求 Yellow 与 Break 外观，不能视为视觉独立。

最终决策：以**一条解析后的 Slide 分支**为互斥边界。普通 Tap/Hold/Touch 按单物件检查；Slide 分支的星头和所有连接段只要任一处出现 `b`，该分支任何位置都不得出现 `y`：

- `1yb`、`1by`：非法；
- `1y-3b[8:1]`、`1b-3y[8:1]`：非法，即使修饰符位于不同组件；
- `1-3y[8:1]-5b[8:1]`：非法，同一连接 Slide 分支内不能混用；
- `1y/2b`：分属 `/` 的两个物件，互斥检查彼此独立；但 `1y` 随后会按 Q7/Q8 的自然 EACH 规则清除 Force Yellow 标志；
- `1y-3[8:1]*-5b[8:1]`：`*` 会解析为两个 Slide 分支，建议分别检查；第一分支可保留 Yellow 星头，第二个无头分支使用 Break 轨迹。

这个边界既保留 Q10 的逐段 Yellow 表达能力，也尊重现有 Break Slide 会影响星头外观的事实。

### Q14：Force Yellow 与 HSpeed 分组配色谁优先？

状态：已回答，按自然 EACH 的既有优先级处理。

代码事实：开启编辑器的“按 HSpeed group 着色物件”后，当前 HSpeed group 颜色会覆盖普通和自然 EACH 的时间轴颜色。若保持现状，带 `y` 的物件在这个诊断模式下也不会显示黄色。

最终决策：`y` 与自然 EACH 共享现有颜色优先级，不提升为最高优先级。未启用 HSpeed group 着色时，`y` 使用 Yellow；启用该诊断模式时，`y` 与自然 EACH 一样可被 HSpeed group 颜色覆盖。Mine/Break 的既有优先级与互斥规则保持不变。

### Q15：MA2 导出如何保留 Yellow？

状态：已回答；原 `!y` 方案扩展为 `!y` + `!yh`，但仍不纳入外部读取器兼容性承诺，也不是当前 MajdataView 的输入协议。

代码事实：标准 MA2 note ID 和当前私有尾字段没有 Yellow 语义；若直接沿用现有导出，`y` 会丢失。连接 Slide 又要求逐段保存 Yellow，因此头记录和对应轨迹记录需要分别处理。

初始决策：增加 Majdata 私有尾标记 `!y`，仿照现有 `!m`：

- 普通物件/星头 `y` 输出到该物件记录尾字段；
- 某个 Slide 轨迹段的 `y` 输出到该段 MA2 轨迹记录；
- 自然 EACH 中已按 Q7/Q8 丢弃的 `y` 不输出；
- 解析器应把 `!y` 与 `!m`、`#groupFspeed` 作为顺序无关的私有尾修饰，并拒绝重复 `!y`。

未实现 Majdata 私有 Yellow 标记的外部读取器如何处理这些字段不属于本设计的兼容性合同，不假设其会忽略、降级或保留。导出器自身不得静默丢弃 `y`，也不因存在 `y` 拒绝导出。

游戏接入后的协议扩展：单独的 `!y` 无法表达无头 Slide 和同头 `*` 各分支的移动星状态，因此新增精确小写标记 `!yh`：

- `!y` 只表示当前 MA2 记录自身使用 Yellow 外观：普通物件/静态星头，或当前 Slide/Wifi 轨迹段；
- `!yh` 只写在当前 Slide/Wifi 分支首个正文记录，表示该分支移动星使用 Yellow 外观；有头、无头和同头后续分支都按各自状态独立写出；
- 连接续段 `CNS*` 不重复 `!yh`；同一首段同时黄化移动星和轨迹时规范输出 `!yh!y`；
- 可见 Yellow 星头同时输出星头记录 `!y` 与其首分支正文 `!yh`，避免同头多个分支错误共享移动星颜色；
- 规范尾顺序为 `!m`、`!yh`、`!y`、`#groupFspeed`。读取器按顺序无关方式识别，但必须先匹配较长的 `!yh`；
- `!m` 与任一 Yellow 标记互斥；重复标记、未知 `!...`、非法记录位置和正文续段上的 `!yh` 均拒绝；`#...` 内容由现有 Soflan 逻辑处理，本协议只移除私有标记并原样透传剩余文本；
- 若未来实现采用该扩展的 MA2 游戏读取器，它仅对旧版“可见星头有 `!y`、首分支缺 `!yh`”做受限兼容：为首分支推导移动星 Yellow 并记录警告。无头或同头后续分支在旧 MA2 中丢失的状态无法恢复。当前 MajdataView 不执行此规则，因为它通过 managed JSON 直接取得两个 Force Yellow 字段。

### Q16：预览运行时是否必须端到端显示 `y`？

状态：已回答；最初阶段延期，后续 MajdataView 接入已完成。

代码事实：MajSimaiX 公共 `SimaiNote` 属性会进入编辑器发送给 MajdataView 的 JSON。最初设计阶段因 MajdataView 位于独立仓库而延期；后续已在 MajdataView 中完成 managed JSON 到运行时 Sprite 的端到端接入。

原阶段决策：先实现 simai 语法、MajSimaiX 解析模型、MajdataEdit 独立语法校验和相关参考文档，MajdataView 运行时单独接入。

后续接入决策：

- `IsForceYellow` 决定普通物件、静态星头以及同一连接 Slide 分支全部移动星的 Yellow 外观；
- `ForceYellowSlideSegmentIndices` 只决定对应轨迹段的 Yellow 外观；
- 无头 Slide 的隐藏星头带 `y` 时，移动星仍显示 Yellow；
- `*` 拆出的分支互不传播 Force Yellow；
- View 内部保留逻辑 `isEach`，仅在素材选择处与 Force Yellow 做 OR；
- 外部 JSON 的非法逐段索引在生成任何物件前严格拒绝，旧 JSON 缺失字段或显式 `null` 仍兼容。

### Q17：本阶段是否同步修改 MajdataEdit 自己的时间轴预览？

状态：已回答，采用推荐方案。

最终决策：同步修改。`MainWindowCore` 的时间轴是当前仓库内的独立消费者，不依赖外部 View；让它读取 Force Yellow 标志可以立即验证星头/普通物件和逐段 Slide 轨迹的作用域。它不等同于 Q16 中暂缓的 MajdataView 接入。

### Q18：本阶段是否同时实现 MA2 `!y`/`!yh` 导出？

状态：已回答，采用推荐方案。

最终决策：实现。`Ma2Export/SimaiChartConverter.cs` 属于当前仓库，且 Q15 已确定扩展协议；本阶段同步写出普通物件/星头和逐段 Slide 轨迹的 `!y`，并为每个 Yellow Slide/Wifi 分支首段写出 `!yh`，保证 MA2 导出不会静默丢失移动星状态。MajdataView 运行时接入是独立链路：它通过 managed JSON 直接读取 `IsForceYellow` 和 `ForceYellowSlideSegmentIndices`，不依赖 `!y`/`!yh`，现已按 Q16 的后续决策完成。

### Q19：逐段 Yellow 在 `SimaiNote` 中如何表示？

状态：已回答，采用推荐结构并按用户要求调整公共属性名。

最终决策：使用两个职责明确的公共属性：

```csharp
public bool IsForceYellow { get; set; }
public int[] ForceYellowSlideSegmentIndices { get; set; } = Array.Empty<int>();
```

- `IsForceYellow` 只表示普通物件或 Slide 星头的 `y`；无头 Slide 可保留该值，静态星头不显示，但 MajdataView 的移动星仍继承该状态。
- `ForceYellowSlideSegmentIndices` 保存当前 `SimaiNote` 内被 `y` 修饰的轨迹段索引，索引按源码顺序从 `0` 开始；Wifi 算一个轨迹段。
- 同头 `*` 已由 MajSimaiX 拆成多个 `SimaiNote`，每个分支各自从段索引 `0` 开始。
- 非 Slide 和没有 Yellow 轨迹的 Slide 使用空数组。索引必须严格递增、不得重复，并且必须小于解析后的轨迹段数。
- `RawContent` 继续保存移除 `y` 后的结构文本，不要求每个消费者再次从字符串识别 Yellow。
- Q7/Q8 的自然 EACH 规范化只清除 `IsForceYellow`，不清除 `ForceYellowSlideSegmentIndices`；轨迹 `y` 是独立组件，不属于 EACH 星头分组。

这比单一轨迹布尔属性能表达连接 Slide，又避免为所有既有 Slide 序列化整组 `false`，也比引入完整的新 Slide 对象层级更小。数组命名和序列化形状会成为公共 API，后续不得无迁移地更改。

### Q20：本阶段是否要求 Native AOT ABI 完整暴露 Force Yellow？

状态：已回答，采用保持旧 ABI 不变的推荐方案。

代码事实：公开的 `MajSimai_Parse` 返回包含 `UnmanagedSimaiNote` 的结构树；当前 x64 ABI 明确保证该结构为 64 字节，并由验证器锁定字段偏移。`IsForceYellow` 理论上可占用现有布尔字段后的一个对齐字节而不改变 x64 大小，但变长的 `ForceYellowSlideSegmentIndices` 无法放入现有结构。直接追加指针/长度会破坏 ABI。

最终决策：本阶段保持现有 `MajSimai_Parse` ABI 不变，Force Yellow 的完整数据契约限定在 managed `SimaiNote`；文档明确旧 native ABI 不暴露 Force Yellow。需要 native 完整支持时另行设计 `MajSimai_ParseV2` 及版本化结构树，不能修改旧入口返回布局。现有 `UnmanagedSimaiNote` 不新增半套 `isForceYellow`，避免 native 调用方只能读取星头却无法读取逐段轨迹状态。

替代方案是在本阶段同时新增完整 V2 native ABI；这需要为 File、Chart、TimingPoint、Note 和释放逻辑建立平行版本，工作量和兼容面明显扩大。

### Q21：普通物件/星头修饰区中的 `y` 是否顺序无关？

状态：已回答，采用推荐方案。

最终决策：小写 `y` 在所属组件的 header 内与可兼容标志顺序无关；大写 `Y` 不识别。边界如下：

- `1yx` 与 `1xy` 等价；
- `1yh[4:1]` 与 `1hy[4:1]` 等价，但 Hold 的 `y` 必须在 `[` 前，`1h[4:1]y` 非法；
- `B1yf` 与 `B1fy` 等价，TouchHold 同理；
- `1y$` 与 `1$y` 等价，`$$` Force Star 同理；
- `1y!-3[8:1]` 与 `1!y-3[8:1]` 等价，`?` 同理；两者都只修饰被隐藏的星头，不修饰轨迹；
- Slide 星头的 `y` 必须位于第一个 Slide mark 前；轨迹位置继续只接受 Q11 的两种形式；
- FixedSoflan 继续使用 Q6 的特殊规则：`y` 位于 `@` 前，`@` 后只允许速度值。

重复、`b/y`、`m/y` 的拒绝规则优先于顺序无关原则。

### Q22：非法 Force Yellow 语法如何报错？

状态：已回答，采用推荐方案。

最终决策：MajSimaiX 对所有能够确认与 `y` 有关的非法形式抛出 `InvalidSimaiSyntaxException`，并保留原始行列位置和 note token；MajdataEdit 的独立 `SyntaxCheck` 同步生成语法错误。不得由 `GetNotes` 的普通失败路径静默跳过物件。

至少为以下类别提供可区分的错误消息：

- `Force Yellow cannot coexist with Break`；
- `Force Yellow cannot coexist with Mine`；
- `Duplicate Force Yellow modifier`；
- `Invalid Force Yellow modifier position`；
- `Force Yellow slide segment index is invalid`（模型/反序列化验证使用）。

大写 `Y` 和孤立的 `y` 可沿用一般非法 note/意外字符错误，不需要被自动纠正为小写。自然 EACH 中清除 `IsForceYellow` 是合法规范化，不产生警告或错误。

### Q23：是否把完整验证与文档矩阵作为本阶段完成门槛？

状态：已回答，采用推荐方案。

最终决策：是。只有以下项目全部完成才视为本阶段交付：

- MajSimaiX 正向解析：Tap、Hold、Touch、TouchHold、Force Star、普通/无头/Wifi/Same-head/连接 Slide、轨迹两种 `y` 位置和逐段索引；
- 组合解析：`x`、`f`、`$`/`$$`、`@`、HS group、fake-each，以及自然 EACH 清除星头 `IsForceYellow` 但保留轨迹索引；
- 拒绝矩阵：`b/y`、`m/y` 分支冲突、同组件重复 `y`、非法位置、大写 `Y`、无效 FixedSoflan 组合；
- MajdataEdit `SyntaxCheck` 与 MajSimaiX 对相同输入保持接受/拒绝一致；
- 时间轴验证普通物件、星头、无头轨迹和连接 Slide 逐段 `Color.Gold`，并遵循自然 EACH/HSpeed 现有优先级；
- MA2 导出验证头记录与各轨迹段的 `!y`、分支首段 `!yh`、规范 `!yh!y` 顺序、同头分支隔离、重复规则和统计不变；
- managed JSON 缺少新字段时默认 `IsForceYellow = false`、索引为空；旧 native ABI 的 64 字节与字段偏移验证保持通过；
- MajdataView 验证普通物件、连接 Slide、`*` 分支、无头 Slide 和 Wifi 的静态星头/移动星/逐段轨迹映射，并在实例化前拒绝非法 JSON 索引；
- 更新 `MajSimaiX/README.md`、仓库内 `simai-skill` 的两份语法参考、MA2 导出参考、MajdataView 运行时参考和本文；仓库版本稳定后再显式同步工作区外安装的个人 skill 副本；
- `dotnet build MajSimaiX/MajSimai.sln -c Debug`、MajSimaiX 验证器和 `dotnet build MajdataEdit.sln -c Debug` 全部通过，仓库既有警告单独记录。

## 暂定验收原则

- 所有规则同时覆盖 MajSimaiX 实际解析与 MajdataEdit 独立语法检查。
- 非 Yellow 既有谱面解析结果、渲染、统计和导出保持不变。
- 非法组合必须产生明确错误，不允许静默忽略 `y`。
- simai 原文保存不得主动删除用户输入的 `y`。
- 最终实现必须具有正向、组合、拒绝、JSON 序列化和 ABI 回归验证。

## 实际验证结果

- `dotnet build MajSimaiX/MajSimai.sln -c Debug`：通过，0 错误；保留项目既有的目标框架与可空性警告。
- `dotnet run --project MajSimaiX/Validation~/MajSimai.SVValidation.csproj -c Debug`：`23/23 validation cases passed`。
- `dotnet build MajdataEdit.sln -c Debug`：通过，0 错误；输出既有资源重复、net6.0 EOL 和可空性警告。
- `dotnet run --project ForceYellowValidation/MajdataEdit.ForceYellowValidation.csproj -c Debug`：`8/8 validation cases passed`，其中分支级用例覆盖有头、无头、连接、同头 `*`、Wifi 与 `!yh!y` 规范顺序。
- `dotnet run --project Validation~/MajdataView.ForceYellowValidation.csproj -c Debug`：`16/16 validation cases passed`，覆盖自然 EACH/Force Yellow 外观合并、全部 Slide mark 计数、连接 Slide 与无头 Slide 的移动星传播、`*` 分支独立、轨迹独立映射、旧 JSON `null`、非法索引拒绝，以及 `JsonDataLoader` 预检顺序、普通物件和 Slide/Wifi 组件传播、各 Sprite 消费点的源码接线回归。
- `dotnet build Assembly-CSharp.csproj`：通过，0 错误；根解决方案因两个同名 `MajSimai` 项目触发既有 `MSB5004`，因此使用 Unity 生成的主程序集项目验证 View 代码。
- Unity AssetDatabase 刷新和域重载后 Console 无 Error/Exception；按正式 `Server` 启动链执行运行时 Sprite 审计，Tap、Hold、Touch、TouchHold、连接 Slide、无头 Slide 与 Wifi 均通过，Game View 可见黄色/普通轨迹和黄色移动星按合同独立组合；`*` 分支独立性由上述纯映射验证锁定。
- 验证覆盖 MajSimaiX 正向/拒绝矩阵、自然 EACH 清除、managed JSON 默认值与 null 归一化、SyntaxCheck、时间轴逐段索引、MA2 `!y`/`!yh` 尾标、统计不变以及 MajdataView 运行时显示。
