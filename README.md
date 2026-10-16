# FieldNavigation 使用文档

> **整合后的接口设计稿，尚未实现。** 本文约定 AF、MG、AZ、BTQ 后续共同使用的接口和职责。下文类型、方法与示例是目标 API，不能直接用于当前版本；实施时按本文迁移，不要求宿主自行拼接现有底层类。

FieldNavigation（FN）负责把角色带到满足要求的位置，并处理途中失败、重试和脱困。四个插件使用同一套实现，通过目标和约束表达差异。FN 继续作为固定提交的源码 submodule 编译进宿主，不增加独立导航插件。

## 1. FN 负责什么

对外只提供三个主要入口：

| 入口 | 完成的工作 |
| --- | --- |
| `Navigate` | 区域内前往目标，包括选点、骑乘、起飞、导航、落地、下坐骑和停稳 |
| `Transfer` | 传送到目的地，包括准备、发起、等待、有限重试和实际抵达确认 |
| `Relocate` | 短距离脱困，包括寻找附近可达点、移动、确认有效位移和停稳 |

每个入口都负责完整流程，内部共用取消、超时、重试预算、路径归属和状态报告。

| FN 负责 | 宿主负责 |
| --- | --- |
| 在目标允许范围内选接近点、落地点，排除失败点 | 选择哪个 NPC、怪物、FATE 或任务 |
| 判断步行、骑乘、飞行，处理禁飞和中转 | 提供目标位置、允许范围及移动限制 |
| 寻路、移动、落地、最后步行、实际到达确认 | 判断业务目标是否仍有效 |
| 移动停滞检测、重算、换点、跳跃或短移恢复 | 攻击、交互、菜单选择、接交任务、交易 |
| 传送等待、重试、切图观察和抵达确认 | 选择传送目的地 |
| 次数、期限、取消、清理和统一结果 | 判断击杀、任务或库存变化，决定失败后的业务处置 |

AF 的范围选落点、MG 的怪物落点、AZ/BTQ 的 NPC 接近都调用 `Navigate`。它们都是“前往一个目标，并在允许范围内到达”，不再维护按插件或目标类型区分的选点、恢复状态机。

NPC 交互、任务实账、黑名单、清仇恨、退本和 qst/AD 重启流程留在宿主。需要短移时调用 `Relocate`，无需自行实现脱困移动。

## 2. 一次接入，三步使用

宿主初始化时传入已获取的 Dalamud 服务，FN 创建内置运行时、vnavmesh、角色动作和传送后端。四个宿主使用相同入口：

```csharp
// 设计 API：hostServices 是已有 Dalamud 服务的集合，不含宿主编写的恢复算法。
var fn = FieldNavigator.Create(hostServices);
```

宿主无需实现 adapter，也无需注册“飞行脱困”“地面脱困”回调。FN 负责读取状态、调用后端、选择恢复策略和确认结果。常用后端的接入代码也由 FN 维护；后端未安装或接口不可用时返回明确原因。

内部可以使用接口隔离不同后端，但这是 FN 的实现细节。以后增加后端时在库内扩展，正常调用者仍只提供请求参数。

使用分三步：

```csharp
// 1. 发起一次操作，保存返回的句柄。
FnOperation operation = fn.Navigate(request);

// 2. 宿主在 Framework.Update 中持续推进，整个 fn 每帧调用一次。
fn.Tick();

// 3. 读取结果。业务按需检查，UI 可低频读取。
OperationSnapshot state = operation.Snapshot;
```

`Tick` 自动读取运行时快照并推进操作。所有原生读取、IPC 和动作都在 Framework 上执行；不使用后台计时器驱动角色移动。UI 只读取不可变快照、提交取消请求。

## 3. 区域内前往：Navigate

### 基本用法

```csharp
var operation = fn.Navigate(new NavigateRequest
{
    Target = targetPosition, // 世界坐标，距离单位为游戏码
    IgnoreNoFlyZones = false, // 默认 false，遵守 FN 禁飞区
    Arrival = new ArrivalOptions
    {
        Radius = 5,
        HeightTolerance = 2,
        RequireGrounded = true,
        RequireDismounted = true,
        SettleDuration = TimeSpan.FromMilliseconds(300),
    },
});
```

FN 自行选合法接近点，必要时骑乘和飞行，抵达附近后落地、下坐骑、完成最后步行并停稳。路径执行结束不等于成功，必须满足 `Arrival`。

`Target` 是实际目标位置，候选接近点和落地点是 FN 的内部结果。宿主通常不提供预选落点，也不用在失败后自己换点。

### 常用参数

| 参数 | 含义 |
| --- | --- |
| `Target` | 必填，目标的世界坐标 |
| `IgnoreNoFlyZones` | 默认 `false`；本次操作是否忽略 FN 禁飞区规则，详见下文 |
| `Arrival.Radius` | 允许的最终距离，默认按三维距离判断 |
| `Arrival.HeightTolerance` | 允许的最大高差，与距离同时检查 |
| `Arrival.RequireGrounded` | 成功时必须落地 |
| `Arrival.RequireDismounted` | 成功时必须下坐骑，也要求落地 |
| `Arrival.SettleDuration` | 成功前连续停稳的时长，重新移动后重新计时 |
| `Movement.Mode` | `Automatic`、`Foot`、`GroundMount`、`Fly` |
| `Movement.AllowMount / AllowFlight` | 自动选方式及恢复时都必须遵守的动作许可 |
| `Movement.WalkDistance / GroundMountDistance` | 自动选择方式的距离阈值，同时考虑高差及实际许可 |
| `LandingRadius` | 距目标多近开始落地，与最终到达半径分别配置 |
| `FlightHeight` | 飞行接近点相对候选地面的高度 |
| `Recovery` | 恢复动作许可和预算，见第 6 节 |

`Arrival.Radius` 同时限制候选终点的偏移范围。很小的半径用于精确点；较大半径允许 FN 在范围内选点。路径可以绕行，最终停点必须满足范围和高度要求。额外同层、禁入范围等限制通过纯托管目标约束接入，不在约束中执行原生动作。

地图飞行许可、当前坐骑是否能起飞和禁飞范围由 FN 分别检查。强制模式无法使用且没有允许的替代方式时返回失败，不擅自突破动作限制。

### 忽略禁飞区

`NavigateRequest.IgnoreNoFlyZones` 默认为 `false`。只有明确设置为 `true`，本次操作才忽略 FN 禁飞目录中的边界限制及由此触发的落地、地面降级；该设置同时适用于选点、主路线和内部脱困，不修改全局配置。

它不改变游戏实际的地图飞行许可、坐骑能力、碰撞或移动条件，也不覆盖 `Movement.AllowFlight = false`。独立的出口中转规则（`FlightExitZone`）和必须经过的指定点仍生效，它们与禁飞区规则分别管理。新操作默认恢复为 `false`，不会继承上次的临时设置。

### 四个宿主如何使用

| 场景 | 宿主给什么 | FN 完成什么 |
| --- | --- | --- |
| AF 接近 FATE 目标 | 具体目标点、允许到达范围、落地要求 | 选落点、导航、落地，坏点换点 |
| MG / AZ 接近怪物 | 实时目标点、接战范围、高差、骑乘要求 | 同一套选点和接近流程 |
| AZ / BTQ 接近 NPC | NPC 位置、交互范围、下坐骑和停稳要求 | 同一套选点和接近流程 |

范围由宿主按实际用途提供，FN 不根据“NPC/怪物/FATE”猜测距离。抵达后，宿主复核目标身份及业务条件，再交互或战斗。

### 移动目标

同一个目标移动时更新原操作：

```csharp
operation.UpdateTarget(latestPosition);
```

FN 决定是否需要重新规划，保留原操作预算。目标失效时取消；改为追踪另一个业务目标时，先结束旧操作，再发起新操作。不要每帧重新 `Navigate`。

禁飞豁免、出口中转、候选点搜索和换落点由 FN 内部处理。宿主不再读 `IntermediateWaypointPending` 等内部阶段来拼接路线。

## 4. 传送：Transfer

四个宿主使用同一个传送确认流程，普通传送和都市传送通过目的地类型区分。

```csharp
var operation = fn.Transfer(new TransferRequest
{
    Kind = TransferKind.Aetheryte, // 都市传送使用 Aethernet
    DestinationId = destinationId,
    TerritoryId = targetTerritory,
    TotalTimeout = TimeSpan.FromSeconds(90),
    MaximumAttempts = 3,
    ResponseTimeout = TimeSpan.FromSeconds(15),
    SettleDuration = TimeSpan.FromSeconds(1),
});
```

`DestinationId` 由对应后端解释，类型之间不混用。FN 通过适配器核验目的地与目标区域；不能解析或不一致时返回失败。

统一流程：等待可传送并停稳 → 发起请求 → 等待执行/咏唱/加载 → 检查实际目的地 → 等待稳定 → 成功。

- 请求返回“接受”只表示已提交，不算抵达。
- 咏唱、后端请求活动或加载期间不重复发起。
- 响应等待结束后，只有重新满足重试条件才再次请求；总期限从第一次启动计算。
- 同地图传送必须提供 `ArrivalPosition` 和 `ArrivalRadius`，或后端可解析的等效目的位置，不能只凭区域相同成功。
- 已经在目的地且满足稳定要求时直接成功，无须再次传送。
- 传送完成后需要继续走到 NPC 时，再调用 `Navigate`；宿主只负责业务衔接。

取消后停止重试和后续自动动作；已经生效的传送或正在切图不保证可撤销。FN 必须观察并处理尚在进行的后端动作，不能让旧请求继续触发下一段导航。

## 5. 短距离脱困：Relocate

用于外部执行器停止后短移，或其他独立的局部脱困需求。普通导航卡住时由 `Navigate` 内部恢复，宿主不用另外启动它。

```csharp
var operation = fn.Relocate(new RelocateRequest
{
    AwayFrom = blockedTargetPosition, // 可省略，由 FN 选择方向
    IgnoreNoFlyZones = false, // 与 Navigate 相同，仅作用于本次操作
    Distance = 4,
    MinimumDisplacement = 2,
    MaximumDisplacement = 8,
    MaximumPathLength = 12,
    TotalTimeout = TimeSpan.FromSeconds(20),
    SettleDuration = TimeSpan.FromMilliseconds(300),
});
```

FN 选择附近可达点，限制路径长度和移动范围，确认至少产生规定的实际位移，抵达并停稳后才成功。找不到点、路径不合法或恢复超时都返回失败；`AwayFrom` 是选方向的参考，不保证可以严格沿反方向移动。

独立 `Relocate` 按实际是否在空中选择候选：地面使用左右后方的可达地面点，空中使用后上/后下方的可达空间点。它直接执行短移，不额外先跳跃；普通导航的“先跳跃，再短移，再重导航”由 `Navigate` 内部管理。不会为了脱困自行起飞。

宿主在调用前停止并确认所属外部执行器停止，避免两个执行器同时移动。完成后由宿主核验是否恢复 qst、AD 或其他业务；脱困成功不代表任务完成或外部执行器已重启。

## 6. 失败重试与预算

### 内置两套移动恢复策略

FN 根据**角色实际是否在空中**选择策略，不按 NPC、怪物、FATE 或宿主名称区分。即使原计划飞行，只要已经落地就使用地面策略；尚未起飞、正在起飞/落地过渡时先等待状态确认。

| 当前状态 | 内置恢复顺序 |
| --- | --- |
| 地面卡住 | 停止所属路径 → 尝试一次允许的跳跃并检查效果 → 仍未脱困则搜索左后/右后方可达地面点 → 短移并停稳 → 从当前位置重新导航到原目标 |
| 飞行卡住 | 停止所属路径 → 搜索后上/后下方可达空间点 → 短移并停稳 → 从当前位置重新导航到原目标 |
| 下降受阻 | 使用落地专用处理，换合法落点重新接近；不能仅靠向上移动就宣告落地成功 |

“后方”优先相对最近一次有效受阻路径的前进方向计算，没有可用方向时参考目标方向，再回退到角色朝向。左右/上下候选都经过可达性和路径检查，排除本轮失败点，再选择可用点，不固定反复撞向同一方向。

地面候选必须有可行走地面并满足局部高差和短移范围；空中候选必须在可达空间内，不能把地面网格贴点直接当作空中终点。短移允许暂时离开最终到达圈，但受移动范围、路径长度、禁飞规则和额外目标约束限制，最终仍须返回原目标范围。

- 跳跃有独立次数和观察期限；请求成功不等于脱困成功，FN 检查实际移动和路径推进。动作不可用或宿主禁止跳跃时跳过该步骤。
- 一个短移候选失败后，只能在剩余预算内换点；候选用尽则失败，不盲目直线移动。
- 脱困过程中落地、重新飞起或进入占用时，取消不适用的旧路径，重新确认状态；切换策略不重置预算。
- 每轮恢复先暂停正常路线，恢复结束再重算原目标路径，两者不能同时发出移动。普通导航失败后的恢复全部由 FN 管理，宿主无需补一次重新 `Navigate`。

### 调用者只配置许可与预算

默认启用上述策略。需要限制动作时传参数，不提供自定义 adapter 或恢复回调：

```csharp
var operation = fn.Navigate(new NavigateRequest
{
    Target = targetPosition,
    IgnoreNoFlyZones = false,
    Recovery = new RecoveryOptions
    {
        TotalTimeout = TimeSpan.FromMinutes(3),
        MaximumRecoveries = 3,
        AllowJump = true,
        AllowRelocation = true,
        AllowGroundFallback = true,
    },
});
```

| 选项 | 作用 |
| --- | --- |
| `TotalTimeout` | 整次前往的总期限 |
| `MaximumRecoveries` | 累计恢复轮数上限，每轮从停滞处理到重导航；轮内跳跃、候选查询和换点另有有限次数与期限 |
| `StallTimeout` | 移动阶段无有效进展多久触发恢复 |
| `PathfindTimeout` | 单次算路期限，不能靠角色位移续期 |
| `AllowJump` | 是否允许地面跳跃恢复；飞行恢复不使用跳跃 |
| `AllowRelocation` | 是否允许地面或空中的短移恢复 |
| `AllowGroundFallback` | 飞行失败后是否允许转地面；仍遵守目标和移动约束 |

方向采样、跳跃观察、短移距离和恢复顺序由 FN 维护统一默认值。常规宿主无需调参；确需扩展参数时四宿主共用同一定义，不增加某插件专属恢复算法。

### 共用的失败边界

三个入口共用预算和结果规则，具体动作按用途选择。重算、换点、跳跃和短移都受原操作预算约束，内部阶段另有独立期限；更新目标或切换地面/空中策略不能续期。

| 情况 | FN 的处理 |
| --- | --- |
| 后端暂未就绪、请求暂被拒绝 | 在预算内等待或重试 |
| 候选点无路径、落点不合法 | 在原目标约束内换点，不直接当作角色卡住而跳跃 |
| 有路径但移动无进展 | 按实际地面/空中状态执行内置恢复 |
| 下降无进展 | 换合法落点，仍受原预算限制 |
| 路线结束但未满足目标要求 | 继续接近、恢复或失败，不报告成功 |
| 无合法候选、次数/期限耗尽 | 清理并失败 |
| 目标失效、角色变化、业务取消 | 结束原操作并清理 |

合法绕路不能仅因离目标暂时更远而判卡住；持续位移也不能让绕圈无限续期。战斗、施法、对话等占用按阶段等待或中止，期间不强行跳跃或短移；清除过期路径和停滞采样，默认总期限继续计算，不能无限等待。

`Navigate` / `Relocate` 遇到区域加载时终止旧区域路径；`Transfer` 则把预期加载作为流程的一部分。登出、死亡、角色更换、依赖失效都必须进入明确的中止或失败流程。

## 7. 状态与结果

三个入口统一返回 `FnOperation`，通过 `Snapshot` 读取状态：

| 字段 | 含义 |
| --- | --- |
| `OperationId` | 本次操作唯一身份 |
| `Status` | `Running`、`Cancelling`、`Succeeded`、`Failed`、`Cancelled` |
| `Phase` | 当前阶段，例如准备、算路、移动、落地、恢复、等待传送 |
| `RecoveryMode` | `None`、`Ground`、`Airborne`，说明当前使用的内置恢复策略 |
| `Reason` / `Message` | 程序判断用的原因码 / 人可读的解释 |
| `Attempts` / `Recoveries` | 请求尝试次数 / 恢复次数 |
| `Elapsed` | 本次操作耗时 |
| `ResolvedDestination` | 当前实际选中的终点，尚未选定时为空 |
| `IsTerminal` | 是否已进入终态并完成 FN 所属资源清理 |

通用原因码包括 `BackendUnavailable`、`NoPath`、`PathfindTimeout`、`MovementStalled`、`LandingFailed`、`GoalNotSatisfied`、`TransferNotConfirmed`、`BudgetExceeded`、`ContextChanged`、`MovementConflict`、`CancelledByHost`。

成功含义按入口区分：`Navigate` 满足实际到达要求；`Transfer` 确认实际目的地并稳定；`Relocate` 完成有效短移并停稳。三者都不代表宿主业务完成。

宿主只处理一次终态：

```csharp
fn.Tick(); // 每帧先推进，即使宿主业务已经停止，也要处理取消清理

if (activeOperation is { } operation && operation.Snapshot.IsTerminal)
{
    var result = operation.Snapshot;
    activeOperation = null; // 先取走，避免下一帧重复交接
    HandleResult(result);   // 成功后执行业务，失败后决定跳过还是停止
}
```

终态快照保留，不会因继续读取而重新执行。回调可作为可选通知：`operation.Completed += HandleResult`，每次操作只通知一次，在 Framework 完成清理后派发；回调中提交的新操作从后续 Tick 开始，避免重入。宿主选择轮询或回调一种方式处理业务交接。

## 8. 取消、互斥与卸载

```csharp
operation.Cancel(); // 提交取消；UI 可调用，原生清理由后续 Framework Tick 执行
```

- `Start` 类入口及目标更新在 Framework 调用；UI 启动请求先交给宿主的 Framework 调度。
- 一个 `FieldNavigator` 同时只执行一个操作。已有活动操作时拒绝新启动，不隐式取消或覆盖。
- 取消请求后进入 `Cancelling`，清理完成才进入 `Cancelled`；不能只把业务字段置空就停止 Tick。
- 异步路径结果带操作/路径代际，取消后的旧结果不能再次发起移动。
- 发现外部移动接管时返回 `MovementConflict`，不能靠全局 Stop 抢回控制权。
- 卸载时在 Framework 调用 `fn.Dispose()`，清理所属查询、路径和订阅；迟到结果失效。已生效的传送按第 4 节处理。

跨插件隔离依赖后端支持 owned 路径。每个宿主编译自己的 FN，库内互斥只约束本实例，不能代替跨插件所有权。当前可选的 vnavmesh owned IPC 应继续使用；旧 IPC 的兼容模式不能承诺严格隔离。

## 9. 迁移约定

按共同能力迁移四个宿主，不以某个宿主的业务恢复流程定义 FN：

1. 统一操作句柄、快照、预算、取消及 FN 内置的运行时/后端接入，宿主无需编写 adapter。
2. 将选接近点/落点、落地换位、失败换点和地面/飞行两套内置恢复全部收进 `Navigate`，落实默认关闭的 `IgnoreNoFlyZones`。
3. 将四宿主的传送等待、重试和抵达确认收进 `Transfer`。
4. 将独立短移统一到 `Relocate`，删除宿主中的重复移动实现。
5. 对照职责表保留业务选择和实账判断，删除已经由 FN 接管的状态机分支。

现有 `TravelJourney`、`AreaLandingPlanner`、`ApproachRetrySession`、`CombatLandingSession`、`ShortRelocationSession` 和可取消路径后端作为整合基础。完成后宿主通常只使用本文的三个入口，底层类不再要求宿主自行组合。

禁飞矩形、路线豁免、指定出口中转和精确落点约束等已有能力必须保留，并在统一入口内处理，不能因接口简化而丢失。

四宿主仍固定同一共享提交及 `field-navigation.version`；通用代码只在独立 FN 仓库修改并提交，再同步宿主子模块指针，禁止维护私有副本。初始化使用 `git submodule update --init --recursive`，不自动追最新分支。

本文是使用接口的设计约定，尚未修改运行时代码；编译和离线检查不代表游戏内验证。
