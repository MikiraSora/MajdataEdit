# Mine 相关实现检查

检查日期：2026-07-17

## 状态图标

| 图标 | 含义 |
| --- | --- |
| ✅ | 已实现，当前代码会识别并消费 Mine 语义 |
| ⚠️ | 部分实现，数据可以到达该模块，但行为不完整或按普通音符降级 |
| ❌ | 未实现，没有 Mine 专用处理 |

## 结论

> ⚠️ **当前 Mine 处于部分实现状态：MajSimaiX 已经能够解析和传递 Mine 标志，但
> MajdataEdit 与当前打包的 MajdataView 尚未完整消费这些标志，因此不能视为端到端支持。**

当前可以读取 Mine 语法、在 `SimaiNote` 中保存标志，并将标志写入发送给 MajdataView
的 JSON。原始 maidata 文本保存路径也会保留 `m`。编辑器语法检查、时间轴和音效开关
现已接入 Mine，不可理检查也会忽略 Mine 物件；MA2 导出会通过 Majdata 私有 `!m` 尾块
保留 Mine 语义，但当前 MajdataView 预览仍缺少完整的 Mine 专用行为。

## 支持矩阵

| 模块 | 状态 | 当前行为 |
| --- | --- | --- |
| MajSimaiX 数据模型 | ✅ | `SimaiNote` 包含 `IsMine` 和 `IsMineSlide` |
| MajSimaiX simai 解析 | ✅ | 能识别普通 Mine 和 Mine Slide 的 `m` 标记 |
| maidata 原文保存 | ✅ | 编辑器保存原始谱面文本，`m` 不会被主动删除 |
| JSON 数据传递 | ✅ | `SimaiNote` 公共属性会随 `timingList` 序列化 |
| Each 分析 | ⚠️ | 已排除 Mine、Mine Slide 和无头 Slide，不参与 Each 分组 |
| 编辑器语法检查 | ✅ | 在 FixedSoflan 校验后规范化 `m`，支持各音符类型的 Mine 修饰 |
| 编辑器时间轴 | ✅ | Mine 和 Mine Slide 统一使用灰色，并优先于 HSpeed 分组配色 |
| 编辑器音效 | ✅ | 设置页可控制是否生成 Mine 物件音效，默认关闭 |
| 谱面不可理检查 | ✅ | 多押与 Slide 检查均忽略 Mine 和 Mine Slide |
| MA2 导出 | ⚠️ | 通过顺序无关的私有 `!m` 尾块保留 Mine 标志，标准 MA2 读取器可能忽略该扩展 |
| 当前打包的 MajdataView | ❌ | 数据类有 Mine 字段，但运行时加载和实例化代码未消费字段 |
| Mine 自动化测试 | ❌ | 当前仓库未找到针对 Mine 解析或编辑器行为的测试 |

## 已有实现

### ✅ 数据模型

[`MajSimaiX/Runtime/SimaiNote.cs`](../MajSimaiX/Runtime/SimaiNote.cs) 定义了：

```csharp
public bool IsMine { get; set; }
public bool IsMineSlide { get; set; }
```

Mine 没有独立的 `SimaiNoteType`。它与 Break、EX 类似，是附加在 Tap、Hold、Slide、
Touch 或 TouchHold 上的布尔标志。

在 .NET 7 及以上目标中，这两个标志还会由
[`UnmanagedSimaiNote`](../MajSimaiX/Runtime/Unmanaged/UnmanagedSimaiNote.cs) 传入非托管结构。

### ✅ simai 解析

[`MajSimaiX/Runtime/SimaiNoteParser.cs`](../MajSimaiX/Runtime/SimaiNoteParser.cs)
的 `NoteFlag.Detect` 会扫描 `m`：

- `m` 位于普通音符部分时设置 `IsMine`；
- `m` 位于 Slide 目标和时长附近时设置 `IsMineSlide`；
- 解析完成后把两个检测结果写入 `SimaiNote`。

当前解析层可表达的典型语法包括：

| 语法 | 解析结果 |
| --- | --- |
| `1m` | Tap，`IsMine = true` |
| `1hm[4:1]` | Hold，`IsMine = true` |
| `B1m` | Touch，`IsMine = true` |
| `B1hm[4:1]` | TouchHold，`IsMine = true` |
| `1-3m[8:1]` | Slide，`IsMineSlide = true` |
| `1-3[8:1]m` | Slide，`IsMineSlide = true` |

MajSimaiX 的功能清单也把 Tap、Hold、Slide、Touch 和 TouchHold 的 Mine 标记列为已支持，
参见 [`MajSimaiX/README.md`](../MajSimaiX/README.md)。

### ✅ 编辑器解析和 JSON 传递

[`SimaiProcess.Serialize`](../SimaiProcess.cs) 使用 `SimaiParser.ParseChart` 生成
`SimaiTimingPoint` 和 `SimaiNote`，因此 MajSimaiX 设置的 Mine 标志会进入编辑器内存模型。

[`MainWindowCore`](../MainWindowCore.cs) 将包含这些 `SimaiNote` 的 `timingList` 交给
Newtonsoft.Json 序列化。由于 `IsMine` 和 `IsMineSlide` 是公共属性，Mine 标志会出现在
发送给 MajdataView 的 `majdata.json` 中。

### ⚠️ Each 分析

[`EachNoteAnalysis.IsCandidate`](../EachNoteAnalysis.cs) 已有明确的 Mine 判断：

```csharp
if (note.IsMine || note.IsMineSlide || note.IsSlideNoHead)
{
    return false;
}
```

这能避免 Mine 被编辑器的 Each 分析当作普通多押成员，但它只解决 Each 分组问题，
不代表其他编辑器功能已经支持 Mine。

## 编辑器实现与剩余风险

### ✅ 语法检查

[`SyntaxModule/SyntaxCheck.cs`](../SyntaxModule/SyntaxCheck.cs) 仍使用独立于 MajSimaiX
解析器的结构检查，但现已在进入 Tap、Hold、Slide 和 Touch 检查前规范化小写 `m`。
处理顺序为：

1. 先校验并移除 FixedSoflan `@` 修饰；
2. 再移除 Mine `m` 修饰；
3. 最后按基础音符结构检查 Tap、Hold、Slide、Touch 或 TouchHold。

该顺序使 `1m@`、`1m@600` 和 `1m@-3[8:1]` 可以通过，同时保证 `1@m` 仍因把 `m`
作为非法 FixedSoflan 速度而被拒绝。Touch 与 TouchHold 的组合标记检查也已同步支持
`b`、`x`、`f`、`h`，因此 `B1m`、`B1fm`、`B1hm[4:1]` 等 Mine 形式不会再被误报。

已通过反射测试覆盖 Mine Tap、Hold、Touch、TouchHold、Slide、FixedSoflan 组合，
以及空 Mine、非法 FixedSoflan、非法键位、缺少 Slide 时长和非法 Hold 时长等拒绝场景。

### ✅ 编辑器时间轴

[`MainWindowCore.DrawWave`](../MainWindowCore.cs) 会同时检查 `IsMine` 和 `IsMineSlide`，
并把 Mine Tap、Force Star、Hold、Touch、TouchHold、Slide 星头及 Slide 轨迹统一绘制
为灰色。

Mine 灰色优先于 Break、Each 和 HSpeed group 颜色；即使启用按 HSpeed group 着色，
Mine 仍保持灰色。Mine TouchHold 还会强制绘制灰色头部，以便在时间轴上明确辨认。

### ✅ 音效开关

[`EditorSetting`](../Majson.cs) 定义了默认值为 `false` 的 `PlayMineSoundEffects`，并在
编辑器设置页提供对应复选框。设置会随其他全局编辑器设置一起保存和恢复。

[`SoundEffect.generateSoundEffectList`](../SoundEffect.cs) 在进入音符类型分支前同时检查
`IsMine` 和 `IsMineSlide`。关闭时跳过整个 Mine 物件，因此不会遗留 Tap/Touch 头音、
Hold 尾音、TouchHold 结束音或 Slide 启动/尾音；开启后按该物件的基础音符类型生成原有
音效。该开关只控制物件音效，不改变 All Perfect 完成时间计算。

### ✅ 谱面不可理检查

[`SubWindow/MuriCheck.xaml.cs`](../SubWindow/MuriCheck.xaml.cs) 在 `multNoteDetect` 和
`slideDetect` 的音符入口同时检查 `IsMine` 与 `IsMineSlide`。Mine 物件不会进入操作序列，
因此不参与多押、Hold 占用、Slide 路径或撞尾检查；Mine Touch/TouchHold 也不会误触发
“不支持 DX 谱面”的中止分支。

### ⚠️ MA2 导出

[`Ma2Export/SimaiChartConverter.cs`](../Ma2Export/SimaiChartConverter.cs) 保持原有基础音符
ID，并在对应 MA2 记录的最后一个字段中使用 Majdata 私有 `!m` 修饰符保存 Mine 标志。
Tap、Hold、Touch、TouchHold 和 Slide 星头读取 `IsMine`；Slide 轨迹读取
`IsMineSlide`，两者不会互相传播。无头 Slide 只会生成轨迹标记；连接 Slide 沿用当前
`SimaiNote` 粒度，其拆出的轨迹行共享 `IsMineSlide`。

`!m` 与 Soflan/FixedSoflan 修饰符的相对顺序不构成语义。导出器规范输出为 Mine 在前，
例如 `!m#12`、`!m#F600`、`!m#12F600`；读取方也应把 `#12!m` 和
`#12F600!m` 识别为等价形式。所有修饰符位于同一个制表符分隔的尾字段中。

该扩展不会创建新的 MA2 音符 ID，也不会改变 `T_REC_*`、`T_NUM_*`、`T_JUDGE_*` 或
`TTM_SCR_*` 汇总。不了解 `!m` 的标准 MA2 读取器仍可能把这些记录当作普通基础物件，
因此此项属于 Majdata 工具链内的扩展兼容，而不是标准 MA2 Mine 类型。

### ❌ 当前打包的 MajdataView

检查对象：
`bin/Debug/net6.0-windows/MajdataView_Data/Managed/Assembly-CSharp.dll`。

使用 ILSpy 检查 `JsonDataLoader`、`NoteManager` 和各 Note Drop 类型后，未发现
`IsMine` 或 `IsMineSlide` 的读取逻辑。`JsonDataLoader` 实例化音符时只传递 Break、EX、
Hanabi、SlideBreak、FixedSoflan 等已支持标志。

因此，虽然配套的 `MajSimai.dll` 数据模型包含 Mine 字段，当前打包的 MajdataView
仍会把 Mine 当作普通音符实例化和计数，不具备 Mine 外观、运动、判定或效果实现。

## 最终判断

- ✅ **可以确认存在 Mine 解析实现。**
- ✅ **可以确认 Mine 标志能够进入编辑器模型和 JSON。**
- ⚠️ **只能称为数据层和局部编辑器适配，不能称为完整 Mine 支持。**
- ✅ **编辑器语法检查已完成 Mine 修饰接入，不再误报典型 Mine 语法。**
- ✅ **编辑器时间轴已使用灰色区分 Mine 和 Mine Slide。**
- ✅ **编辑器设置页可以控制 Mine 物件音效，且默认不播放。**
- ✅ **谱面不可理检查会完全忽略 Mine 和 Mine Slide。**
- ⚠️ **MA2 导出已通过私有 `!m` 尾块保留 Mine 标志，但不属于标准 MA2 扩展。**
- ❌ **当前预览仍未完成 Mine 接入。**

要达到端到端支持，仍需定义 Mine 的预览语义，在 MajdataView 中消费两个 Mine 标志，
并让所有需要读取扩展 MA2 的组件按顺序无关规则识别 `!m`。
