# SV 到 HS/Soflan 兼容设计

状态：已实现并通过本地 parser 验证。本文记录最终协议，不再保留早期的 SV 乘法模型候选方案。

## 最终语义

`<SV*x>` 是一个大写、瞬时的速度声明。解析器把它归一化为默认 Soflan group `0` 的
`HSpeed = x` 事件；不存在独立的 `SVeloc` 字段，也不会把 `x` 乘到当前 HS 上。

```text
<SV*2>       group 0 HSpeed = 2
<SV*0>       停止视觉时间轴
<SV*-1>      反向视觉时间轴
<SV*1>       显式恢复默认速度
```

`<SV*x>`、`<HS*x>` 和 `<HS0*x>` 共享 group `0` 的事件线。它们在同一实际 timing 的
执行顺序由谱面文本顺序决定，最后一个声明覆盖前面的声明，并为该时刻保留一个空
`NoteTiming`。速度持续到下一次 group `0` 的 HS/SV 声明。非零 HS group 仍各自维护独立
的速度线；非零 group 的 HS 不会改变 group `0`。

SV 标签可以出现在音符前或后，也可以与 `/` 多押共用一个 timing。fake-each 的每个
展开子项按自己的实际 `Timing` 查询最终速度，因此跨越逗号槽位的子项会看到后续的
HS/SV 覆盖。已有 HS 插值的区间删除/覆盖规则保持不变：后解析的插值可以覆盖区间内
较早的 SV 事件。

`CommaTimings` 仍表示原始逗号时间轴，`HSpeed` 始终为 `1`；SV 只进入归一化后的
`NoteTimings`/空 HSpeed 点。音频时间、BPM、判定时间、Hold/Slide 时长和非托管结构均
不变。

## 接受与拒绝

SV 只接受与瞬时 `<HS*x>` 相同的 invariant-culture 浮点字面量，包括 `0`、负数、正号、
指数以及 HS 当前接受的 `NaN`/`Infinity` 特殊值。数值不会增加 SV 专属范围限制。

| 输入 | 结果 |
| --- | --- |
| `<SV*2>`、`<SV* 2>`、`<SV*1e2>` | 合法瞬时 group 0 事件 |
| `<SV*>`、`<SV*abc>` | 分别为语法错误、数值 markup 错误 |
| `<SV0*2>`、`<SVg*2>`、`<SV?>`、`<SV?*2>` | `InvalidSimaiSyntaxException` |
| `<SV*2[4:1]>`、`<SV*2~1>`、`<SV*2[4:1]easeIn>` | `InvalidSimaiSyntaxException`；SV 不支持 duration、插值链或 easing |
| `<SV*2>(1,...)` | `InvalidSimaiSyntaxException`；SV 不支持 group scope（`(120)` 仍是普通 BPM 声明） |
| `<SV*2` | `InvalidSimaiSyntaxException`；标签必须闭合 |
| `<sv*2>` | 不识别为 SV，按非法 markup 处理；只有大写 `SV` 合法 |

HS 的 group、插值、easing、自动 group 和 FixedSoflan 规则没有改变。HS/SV 在 group
scope 内都被拒绝。标签中的换行处理与现有 HS 相同，数值使用
`NumberStyles.Float` 和 `CultureInfo.InvariantCulture`。

## lowercase `c`

进入 FixedSoflan `@` 校验和 note flag 检测前，`SimaiRawTimingPoint` 会从 raw note 中移除
所有 lowercase `c`。因此 timing 和 note 两层的 `RawContent` 都不再包含 `c`，而
`1c`、`1-3[8:1]c`、`1c@600-3[8:1]` 与去掉 `c` 的对象等价。大写 `C` 中央 Touch、
metadata 和 SV 数值内容不受影响。项目不新增 `canSVAffect` 字段，也不实现该修饰符的
原始 Mine_View 语义。

## 来源差异与限制

旧 Mine_View 将 SV 保存为独立倍率并在另一个时间积分路径中使用；本项目选择
MajSimaiX 原生的 HSpeed 事件模型，把 SV 视为 group `0` 的直接覆盖。因而不承诺旧工程
在混合 HS、插值、非零 group 或逐帧采样上的等价结果。下游 MajdataView、SimpleSoflanFramework
和 MA2 导出器只消费归一化后的 `HSpeed/SoflanGroup`，不需要认识 `SV`。

来源代码核对：[MajdataMine_View `JsonDataLoader`](https://github.com/RevoBleug/MajdataMine_View/blob/3b9340c8b2d78f352a9e033da167f77d92eab0f0/Assets/Scripts/JsonDataLoader.cs)
和 [`AudioTimeProvider`](https://github.com/RevoBleug/MajdataMine_View/blob/3b9340c8b2d78f352a9e033da167f77d92eab0f0/Assets/Scripts/AudioTimeProvider.cs)。

## 修改范围

- `MajSimaiX/Runtime/SimaiParser.cs`：识别 SV、写入 group 0 事件/空 timing、同槽合并、闭合标签检查，并让 HS0 更新 group 0 当前状态。
- `MajSimaiX/Runtime/SimaiRawTimingPoint.cs`：在 `@` 和 note flag 解析前规范化 lowercase `c`。
- `SyntaxModule/SyntaxCheck.cs`：采用相同的 SV 接受集、错误边界和 `c` 规范化。
- `Mirror.cs`：把 HS/SV 都视为变速命令并拒绝镜像，错误信息为“包含 HS/SV 变速命令的谱面不支持镜像”。
- `MajSimaiX/Validation/`：net8、无第三方测试框架的可执行验证器。

没有修改 `SimaiTimingPoint` ABI、音频时间、判定逻辑、MajdataView 或
SimpleSoflanFramework。

## 验证

本地命令：

```powershell
dotnet build MajSimaiX\MajSimai.sln -c Debug
dotnet run --project MajSimaiX\Validation\MajSimai.SVValidation.csproj -c Debug
dotnet build MajdataEdit.sln -c Debug
```

验证器覆盖普通/HS/HS0/group/插值/fake-each 基线、SV 持久化和恢复、纯 SV 空点、同槽
顺序、非零 group 隔离、跨槽 fake-each、后续 HS 插值覆盖 SV、非法标签、特殊数值、`c`
等价性、大写 `C` Touch 和 SV/HS 空白兼容性。当前结果为 `13/13 validation cases passed`；构建仅保留仓库已有警告。

## 回滚

产品侧先回滚父仓集成提交，再同步子模块：

```powershell
git revert <parent-integration-commit>
git submodule update --init MajSimaiX
```

这会恢复旧 parser；若需要同时撤销子模块分支历史，再单独 revert 对应的 MajSimaiX
提交。回滚验收标准是 `<SV>` 回到旧的不兼容行为、lowercase `c` 不再规范化，并且所有
无 SV 的 HS/普通基线快照保持不变。
