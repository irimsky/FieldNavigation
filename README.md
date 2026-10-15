# FieldNavigation

AutoFatre、MobGrinder、AutoBTQ、AutoZodiac 共用的野外导航源码库，MIT 许可。作为固定提交的 Git submodule 编译进各插件，没有独立 DLL，也不另装导航插件。

## 谁负责什么

| 功能 | 负责方 | 入口/参数 |
| --- | --- | --- |
| 选择 NPC、怪物、FATE、理符目标；决定业务是否仍有效 | 宿主 | `TravelRequest.Destination`、`TravelObservation.TargetValid/TargetPosition` |
| 按距离/高差选择步行、地面坐骑、飞行 | FN | `TravelModePolicy`；可指定强制方式 |
| 召唤坐骑、等待实际骑乘、起飞、有限重试 | FN | `TravelJourney`、`MountAdapter`、`TakeoffAdapter` |
| 飞行接近高度、进入多少码开始落地 | FN | `FlightHeight`、`LandingRadius` |
| 禁飞边界、地面降级、落地和恢复 | FN | `NoFlyTravelPolicy`、`NoFlyZoneCatalog`、`NavigationTravelSession` |
| 离区时先飞过指定出口，再继续原目标 | FN | `FlightExitZone`、`IntermediateWaypoint` |
| 指定点落地、四点换位下降、最后步行、下坐骑、停稳 | FN | `LandingPoint`、`LandingStrategy`、`DirectGroundApproach` |
| 区分路线终点与实际目标范围/高度，确认最终到达 | FN | `TravelArrivalPolicy`、`RequiredArrivalPoint` |
| 可取消纯算路、延迟结果丢弃、路线长度/起点偏移检查 | FN | `WaypointTravelBackend` |
| 发现停滞、跳跃恢复、请求拒绝/算路/落地/总预算 | FN | `NavigationTravelSession`、`TravelRequest` |
| 范围内地面候选、排除失败落点、有限换点 | FN | `AreaLandingPlanner`、`ApproachRetrySession` |
| 约 4 码短距离脱困、12 码路线限制、实际位移及停稳 | FN | `ShortRelocationSession` |
| 是否交互、战斗、同步等级、接交任务、暂缓目标、传送、重启 qst | 宿主 | 收到 FN 结果后执行业务，并在动作前重新核验实际目标 |

## 完整前往入口

`TravelJourney.Start(TravelRequest, TravelObservation)` 开始一个有总预算的操作；宿主每个 Framework tick 提供一次不可变观察并调用 `Tick`。不传 `arrived` 布尔值，库自行判断。

```csharp
journey.Start(new TravelRequest
{
    Destination = npcPosition,
    FindApproachPoint = true,
    Movement = new() { WalkDistance = 20, GroundMountDistance = 25 },
    LandingRadius = 5,
    Arrival = new() { Radius = 5, HeightTolerance = 2,
        RequireGrounded = true, RequireDismounted = true },
}, observation);

var update = journey.Tick(observation);
// Arrived 后宿主复核 NPC 身份/目标有效性，再开始自己的交互。
```

状态为 `Preparing → Travelling → Landing → Dismounting → FinalApproach → Settling → Arrived`；实际状态可以跳过不需要的步骤。换目标、切换地面/飞行和内部重新规划不会刷新总预算。终态到达仍重新核验范围和骑乘状态。`FlightHeight` 是飞行接近点相对落地目标的高度；它和最终到达范围、落地触发距离分别配置。

`TravelObservation.CanFly` 表示当前地图已允许飞行，使用实际运行时的 `PlayerState.CanFly`，不要传要求已骑乘的 `Control.CanFly`。后者只由起飞适配器核验。角色/区域变更先于任何完成路径的释放；host在登出、加载、依赖失效、死亡、业务取消时调用 Framework 上的 `Cancel`。UI只调用 `RequestCancel`，不得从UI读写原生或Hook。宿主业务不活跃时仍需完成已请求的Framework清理。

禁飞区可在 `NoFlyMapRectangle.SpecialRules` 中声明路线豁免：`InsideToInside`、`InsideToOutside`、`OutsideToInside` 可以任意组合。没有声明时沿用默认禁飞行为。宿主在创建路线时调用 `NoFlyZoneCatalog.IsRouteExempt`，把结果放入 `TravelObservation.NoFlyExempt`；FN 会在整条路线中保持该起点/终点判定，避免跨过边界后重新分类。

`FlightExitZone` 是独立于禁飞区的地图范围：`Routes` 位掩码按 `InsideToOutside`、`OutsideToInside` 声明需要中转的穿越方向，默认只处理内到外。命中方向的路线，宿主把 `FindFlightExitWaypoint` 返回的水平世界坐标写入 `TravelRequest.IntermediateWaypoint`，并设置 `IntermediateWaypointIsMapCoordinate = true`。FN 查询地面高度，取地面上方至少4码与当前高度的较高者作为飞行高度，保留指定X/Z。实际飞到中转点3码内（`IntermediateWaypointRadius`）后才继续原目标；途中经过原目标不能提前落地或完成。两段共用可取消旅程和总时间预算，中转停滞或飞行条件失效时返回失败，交给宿主恢复，不降级为直接前往原目标。其他方向和范围内目标不增加中转；宿主禁用飞行时不传中转点。

当前叹息海（Territory 959）配置 `(4.2, 17.5)`—`(39.2, 4.1)`，出口 `(21.8, 18.5)`，内到外和外到内都要求先经出口；原东西两块禁飞矩形已移除。宿主在远行切换局部落点搜索前检查 `TravelJourney.IntermediateWaypointPending`，并在同一 FATE 内用 `TravelJourney.IntermediateWaypointReached` 记录已通过中转，保证中转完成前不取消该旅程、通过后也不再重复中转。

`AreaLandingPlanner.Update` 可传入 `Func<Vector3, bool>`，按每个已解析的候选地面选择预查的飞行/地面模式。宿主应与后续 `TravelRequest.Movement` 使用同一策略，并按该候选的起点/终点判断禁飞豁免，避免仅凭当前位于禁飞区就把空中起点交给地面寻路。待查模式改变时取消查询并重试该候选，保留原搜索预算；原固定 `bool` 入口继续兼容。

`WaypointTravelBackend.Pump` 仅在同一 Framework tick、完成生命周期/取消判断后调用。`TravelJourney` 自动泵送；直接使用兼容的 `NavigationTravelSession` 时宿主自行泵送。纯算路任务不会移动，任务 continuation 只观察异常。默认查询30秒、起点偏移3码，路径长度可限制。

`NavigationTravelSession` 保留为较低层的兼容入口；原 `NavigationTravelFrame.Arrived` 仅供尚有特殊业务终点条件的调用者。新的一般前往不要在宿主重新写一套状态机。纯战斗位移/护送/采集物交互的业务抢占条件仍由宿主保留。

## 路径归属与 vnavmesh

FN 优先使用可选的 `vnavmesh.Path.MoveToOwned(Guid,List<Vector3>,bool)`、`Path.IsRunningOwned(Guid)`、`Path.StopOwned(Guid)`。每次执行生成新令牌；停止只影响同一令牌，owned 路线卡住不会触发 vnavmesh 的 SimpleMove 自动重试。配套实现位于同级 `ffxiv_navmesh` 的 FollowPath/IPCProvider；这不是要求修改全局 StopOnStuck/RetryOnStuck 配置。

旧版 vnavmesh 仍可使用原有 `Path.MoveTo/Stop`。旧 IPC 没有跨插件所有权标识，而且它的 RetryOnStuck 可能自行重算；因此旧版只能做到 FN 自身纯算路取消，不保证所有跨插件接管场景的严格停止隔离。配套 vnavmesh 源码已生成 Debug，未在游戏安装/启用新接口；不要把本地编译视为游戏已具备该能力。

## 固定同一版本

四个宿主都只引用本仓库固定提交。宿主根目录 `field-navigation.version` 记录已审阅的提交，项目导入 `FieldNavigation.targets`；构建时拒绝不同 HEAD 或已跟踪导航源码的未提交修改。正常初始化用 `git submodule update --init --recursive`，不要用 `--remote` 自动追最新分支。

修改通用逻辑时，在独立 FieldNavigation 仓库的功能分支修改并提交，然后四个宿主同时更新 submodule 指针和 `field-navigation.version`。不得在某个宿主子模块内维护私有副本。先发布/推送共享提交，再推送引用它的宿主；本次只做本地整合，尚未推送。

## 验证边界

四宿主正常构建；MG的导航相关测试覆盖到达、取消迟到结果、错误高度、预算、恢复及旧接口。AF/AZ按项目约定不运行测试。所有原生动作、真实地图路线、阻挡下降、飞行条件及插件接管仍需游戏内验证，编译与离线测试不代表实机通过。
