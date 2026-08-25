# 仓位模拟器外部自动化控制接口开发说明

## 目标

让独立 `slots-simulator` 进程既保留真实的 Modbus TCP IO 数据面，又能被自动化测试程序从外部控制物理环境，从而支持以下无人值守联调：

1. HMI 通过 Modbus 写开锁 DO、读取锁反馈和光幕 DI；
2. 测试程序通过本接口模拟人工放货、取货、关门和故障；
3. HMI 观察 DI 变化并完成或阻断业务；
4. 测试程序查询模拟器快照并断言全过程。

当前开发起点：`slots-simulator/main@0a778c9439d7c0fb0f25791ccce00b5caf7e6b4b`。现有 Modbus 基线 18/18 测试必须保持通过。

## 强制边界

- HMI 与模拟器之间的生产式 IO 通道只能是 Modbus TCP。
- HTTP 只控制测试环境和故障，不提供“开锁”业务接口。
- 开锁必须由 HMI 写 Modbus DO 触发，测试程序不得绕过 HMI。
- HTTP 默认只监听 `127.0.0.1:58006`，非 loopback 监听默认拒绝启动。
- 模拟器 PASS 只证明软件 IO 闭环，不代表真实模块、接线、锁、光幕或车辆合格。
- 所有状态变更必须经过同一个 `SimulatorEngine`，WPF、Modbus 和 HTTP 不得各维护一份状态。

## 推荐进程结构

保留现有项目：

- `src/SQCD_8005AGV_Simulator.Core`：配置、`SimulatorEngine`、Modbus 服务和物理状态；
- `src/SQCD_8005AGV_Simulator`：人工操作 WPF；
- `tests/SQCD_8005AGV_Simulator.Tests`：当前自动化测试。

建议新增：

- `src/SQCD_8005AGV_Simulator.AutomationHost`：ASP.NET Core/Kestrel 无界面进程，同时组合 `SimulatorEngine`、Modbus TCP 和 HTTP 控制面；
- `tests/SQCD_8005AGV_Simulator.AutomationTests`：启动真实 HTTP 与 Modbus 端口的黑盒测试。

自动联调使用 `AutomationHost`，人工调试继续使用 WPF。两者必须复用 Core，不复制模拟逻辑。后续如需 WPF 与 HTTP 同时运行，再让 WPF 连接同一个 Host；首期不要在两个进程内各自创建 Engine。

## HTTP 契约

Base URL：`http://127.0.0.1:58006/api/v1`

每个响应至少包含：

- `schemaVersion`
- `instanceId`
- `runId`
- `revision`
- `observedAt`

每次状态变化必须严格递增 `revision`。写操作接受可选 `expectedRevision`；不匹配返回 HTTP 409，禁止测试步骤在旧状态上继续运行。

### 健康与状态

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health` | 进程、Modbus 监听和 Engine 状态。 |
| `GET` | `/snapshot` | 返回全局故障、Modbus 端点及八仓完整状态。 |
| `POST` | `/reset` | 建立新的 `runId`，清除故障和延迟任务，恢复安全空仓状态。 |

`snapshot.slots[]` 至少返回：

- `slotNo`（1～8）
- `doorState`：`OPEN | CLOSED`
- `cargoState`：`EMPTY | OCCUPIED`
- `unlockOutputRaw`
- `lockFeedbackRaw`
- `lightCurtainRaw`
- `lockFeedbackPending`
- `lightCurtainFeedbackPending`
- `faults[]`

### 模拟外界物理动作

| 方法 | 路径 | 请求示例 | 约束 |
| --- | --- | --- | --- |
| `PUT` | `/slots/{slotNo}/cargo` | `{ "state": "OCCUPIED", "expectedRevision": 10 }` | 只接受 `EMPTY/OCCUPIED`。 |
| `POST` | `/slots/{slotNo}/close-door` | `{ "expectedRevision": 11 }` | 触发既有锁反馈延迟模型。 |

正常联调不提供任意 `open-door` 或 `unlock` HTTP 接口。仓门打开必须来自 HMI 的 Modbus 开锁输出和既有自动弹门模型。

### 传感器故障注入

| 方法 | 路径 | 请求示例 |
| --- | --- | --- |
| `PUT` | `/slots/{slotNo}/lock-feedback-override` | `{ "mode": "AUTO|FIXED_0|FIXED_1", "expectedRevision": 12 }` |
| `PUT` | `/slots/{slotNo}/light-curtain-override` | `{ "mode": "AUTO|FIXED_0|FIXED_1", "expectedRevision": 13 }` |

必须区分内部真实物理状态和覆盖后的 DI。覆盖传感器不能偷偷改变货物或仓门事实。

### Modbus 通信故障注入

| 方法 | 路径 | 请求示例 |
| --- | --- | --- |
| `PUT` | `/faults/modbus` | `{ "mode": "NORMAL|NO_RESPONSE|DISCONNECT|DELAY", "delayMs": 500, "expectedRevision": 14 }` |

- `NO_RESPONSE`：保持 TCP 连接但不返回请求；
- `DISCONNECT`：主动断开当前客户端，并可拒绝新连接；
- `DELAY`：确定性延迟，不使用随机延迟；
- `NORMAL`：恢复通信，不修改仓位物理事实。

## 状态机与并发要求

- HTTP、WPF 与 Modbus 的并发访问必须串行化到 Engine 的同一锁或消息队列。
- `reset` 必须取消所有旧 Pulse、锁反馈和光幕反馈延迟任务，旧任务不得污染新 `runId`。
- 同一 `runId + expectedRevision + command` 的重复请求应幂等；相同测试命令身份但内容不同应返回 409。
- 所有 API 错误使用稳定 `reasonCode`，不能只返回中文字符串。
- API 日志不得记录凭据；本接口不需要生产凭据，但必须限制 loopback。

## 必须新增的自动化测试

1. HTTP reset 返回八仓安全空状态和新 `runId`。
2. HMI/测试客户端写 FC05 后，快照显示对应开锁输出和仓门变化。
3. HTTP 放货、关门后，Modbus FC02 返回与配置极性一致的 DI。
4. `expectedRevision` 冲突稳定返回 409，状态不变。
5. 传感器覆盖只改变 DI，不改变内部货物和仓门事实。
6. `NO_RESPONSE`、`DISCONNECT`、`DELAY` 均可确定性触发并恢复。
7. reset 取消旧延迟任务和故障，旧任务不跨 `runId` 生效。
8. 两个客户端并发请求不会产生 revision 倒退或丢更新。
9. HTTP 不存在开锁接口；任何未知或越权路径返回 404/405。
10. 原有 Modbus 测试 18/18 继续通过。

## 完成条件

- 提交中包含 API OpenAPI 文档或等价机器契约；
- 可一条命令启动无界面 AutomationHost；
- 测试程序能够只通过 HTTP 控制环境，并让真实 HMI 只通过 Modbus 观察结果；
- 全部新旧测试 PASS；
- README 写明人工模式与自动化模式的启动命令、端口和资格边界；
- 将实现 commit、配置哈希、测试结果和已知限制交给 Onboard 联调负责人。

