# FieldNavigation

可复用的野外导航源码目录，供宿主插件作为源码 submodule 引用。

本目录不是独立项目：没有 `.csproj`、没有 DLL，也不包含目标选择、战斗或其他业务逻辑。宿主插件直接把源码编译进自身项目。

`NavigationTravelSession` 是主要入口，负责一次导航任务的状态机，包括飞行与地面切换、禁飞区处理、落地稳定、无位移恢复和失败结果。

宿主插件负责目标选择、业务状态、战斗、传送，以及导航结果对应的业务处理。

`WaypointTravelBackend` 可作为 `NavigationTravelSession` 的后端，把共享导航请求转换为可取消的纯算路任务和显式路径执行。宿主先 Tick 会话完成禁飞区/取消判断，再在同一 Framework 调用 `Pump`；取消后的旧任务只观察异常，永远不会执行迟到路径。`Stop` 只停止本后端已发出的路径；`DirectGround` 支持不查询网格的直接单点步行。宿主 UI 停止先 `CancelCalculation`，后在 Framework 停止整个会话。

`MountAdapter` 和 `TakeoffAdapter` 提供坐骑、下坐骑及稳定一秒后只请求一次起飞；实际 InFlight 仍由宿主观察。Frame 的 `MovementReady` 用于阻止坐骑未就绪时的普通路线请求，同时保留禁飞检测与落地推进。`LandAtDestination` 在宿主确认到达后垂直下降并等待落地稳定；`CompleteAtResolvedDestination=false` 让严格的 NPC/理符业务范围成为唯一到达依据。禁飞区内的地面卡住恢复不会重新切回飞行。

需要在寻路完成后核验整条路线再开始移动的宿主，可使用 `IWaypointNavigationBackend` 与 `VnavmeshWaypointIpc`。`FindPath` 通过可取消IPC只计算路径，返回任务本身不会启动移动；`MovePath` 单独执行宿主已核验的路径，也支持无需网格的单点直接步行。`IsLoaded` 与 `IsReady` 分开表示接口可用和网格已就绪，`IsBusy` 同时检查其他寻路与路径执行；`StopPath` 将IPC错误交给宿主自己的清理策略。宿主保持原角色、区域、坐骑、到达范围及停止归属校验。
