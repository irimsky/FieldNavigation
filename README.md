# FieldNavigation

可复用的野外导航源码目录，供宿主插件作为源码 submodule 引用。

本目录不是独立项目：没有 `.csproj`、没有 DLL，也不包含目标选择、战斗或其他业务逻辑。宿主插件直接把源码编译进自身项目。

`NavigationTravelSession` 是主要入口，负责一次导航任务的状态机，包括飞行与地面切换、禁飞区处理、落地稳定、无位移恢复和失败结果。

宿主插件负责目标选择、业务状态、战斗、传送，以及导航结果对应的业务处理。

需要在寻路完成后核验整条路线再开始移动的宿主，可使用 `IWaypointNavigationBackend` 与 `VnavmeshWaypointIpc`。`FindPath` 通过可取消IPC只计算路径，返回任务本身不会启动移动；`MovePath` 单独执行宿主已核验的路径，也支持无需网格的单点直接步行。`IsLoaded` 与 `IsReady` 分开表示接口可用和网格已就绪，`IsBusy` 同时检查其他寻路与路径执行；`StopPath` 将IPC错误交给宿主自己的清理策略。宿主保持原角色、区域、坐骑、到达范围及停止归属校验。
