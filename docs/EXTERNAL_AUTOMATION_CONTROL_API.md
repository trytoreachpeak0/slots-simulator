# 仓位模拟器外部自动化控制接口开发说明

## 目标

让独立 `slots-simulator` 进程既保留真实的 Modbus TCP IO 数据面，又能被自动化测试程序从外部控制物理环境，从而支持以下无人值守联调：

1. HMI 通过 Modbus 写开锁 DO、读取锁反馈和光幕 DI；
2. 测试程序通过本接口模拟人工放货、取货、关门和故障；
3. HMI 观察 DI 变化并完成或阻断业务；
4. 测试程序查询模拟器快照并断言全过程。

当前开发起点：`slots-simulator/main@0a778c9439d7c0fb0f25791ccce00b5caf7e6b4b`。现有 Modbus 基线 18/18 测试必须保持通过。

## 实施状态（2026-08-25，本地待提交）

- WPF模拟器现已在一个进程内复用同一`SimulatorEngine`，同时提供Modbus `1502`和HTTP `58006`。
- `SQCD_8005AGV_Simulator.AutomationHost`已调整为可嵌入的HTTP宿主组件，不再创建独立Engine、Modbus服务或独立模拟器进程。
- 已实现本文全部HTTP端点、`runId/revision`、命令幂等、Reset回执及 `NORMAL/NO_RESPONSE/DISCONNECT/DELAY` 故障模式。
- 已提供机器契约 `src/SQCD_8005AGV_Simulator.AutomationHost/openapi.json`，运行时地址为 `/openapi/v1.json`。
- 已新增真实HTTP+Modbus端口黑盒测试项目 `SQCD_8005AGV_Simulator.AutomationTests`。
- 当前本地验证：Release构建0错误、0警告；原Modbus测试18/18通过；新增自动化测试14/14通过。
- 上述实现尚未提交或推送；正式实现身份应以负责人审查后的Git commit为准。

## 强制边界

- HMI 与模拟器之间的生产式 IO 通道只能是 Modbus TCP。
- HTTP 只控制测试环境和故障，不提供“开锁”业务接口。
- 开锁必须由 HMI 写 Modbus DO 触发，测试程序不得绕过 HMI。
- HTTP 默认只监听 `127.0.0.1:58006`，非 loopback 监听默认拒绝启动。
- 模拟器 PASS 只证明软件 IO 闭环，不代表真实模块、接线、锁、光幕或车辆合格。
- 所有状态变更必须经过同一个 `SimulatorEngine`，WPF、Modbus 和 HTTP 不得各维护一份状态。

## 进程结构

保留现有项目：

- `src/SQCD_8005AGV_Simulator.Core`：配置、`SimulatorEngine`、Modbus 服务和物理状态；
- `src/SQCD_8005AGV_Simulator`：唯一可运行的WPF模拟器，组合Engine、Modbus和HTTP宿主；
- `src/SQCD_8005AGV_Simulator.AutomationHost`：供WPF引用的ASP.NET Core/Kestrel HTTP宿主组件，只提供HTTP控制面，不拥有状态；
- `tests/SQCD_8005AGV_Simulator.Tests`：当前自动化测试。
- `tests/SQCD_8005AGV_Simulator.AutomationTests`：启动真实 HTTP 与 Modbus 端口的黑盒测试。

本地联调只启动WPF。界面人工操作、上位机程序的Modbus请求及同事程序的HTTP请求全部进入同一Engine；禁止在另一个进程中再创建第二套Engine和Modbus监听。

## HTTP 契约

Base URL：`http://127.0.0.1:58006/api/v1`

每个响应至少包含：

- `schemaVersion`
- `instanceId`
- `runId`
- `revision`
- `observedAt`

所有改变状态的请求必须包含：

- `runId`：请求所针对的当前测试轮次；
- `commandId`：调用方生成的非空、不超过128字符、大小写敏感的命令唯一标识；
- `expectedRevision`：调用方最后观察到的版本号。

写响应除公共字段外还必须包含：

- `commandId`
- `changed`：本次是否真的改变了可观察状态；
- `replayed`：是否为已执行命令的幂等重放；
- `appliedRevision`：该命令首次执行结束后的版本号。

每次可观察状态变化必须严格递增 `revision`。`expectedRevision` 不匹配时返回 HTTP 409，禁止测试步骤在旧状态上继续运行。命令判重必须先于 `expectedRevision` 校验，使已成功执行但响应丢失的命令能够使用原 `commandId` 安全重试。

### 已确认的命令身份与幂等规则

幂等键固定为服务端当前 `instanceId + runId + commandId`。其中 `instanceId` 由服务端配置确定，不要求客户端在请求体内重复提交。服务端对命令的 HTTP 方法、规范化路径和业务请求字段计算内容指纹；`runId`、`commandId`、`expectedRevision` 不计入业务内容指纹。

处理顺序固定为：

1. 校验 `runId` 是否指向当前运行轮次；不匹配返回 HTTP 409，`reasonCode=RUN_ID_MISMATCH`。
2. 按 `runId + commandId` 查询已执行命令。
3. 已存在且内容指纹相同：不再执行，返回原业务结果，`replayed=true`；即使原 `expectedRevision` 已经过期，也不得返回 revision 冲突。
4. 已存在但内容指纹不同：返回 HTTP 409，`reasonCode=COMMAND_ID_CONFLICT`，状态不变。
5. 命令不存在时再校验 `expectedRevision`；不一致返回 HTTP 409，`reasonCode=REVISION_CONFLICT`，状态不变。
6. 校验通过后执行一次，并缓存业务结果和 `appliedRevision`。

`POST /reset` 同样必须幂等。成功 Reset 后虽然会生成新 `runId` 并清除旧轮次普通命令缓存，但必须在实例级保留最近一次成功 Reset 的命令回执；相同 Reset `commandId` 重试时返回同一个新 `runId`，不得再次创建测试轮次。新的不同 Reset 命令成功后可替换上一条 Reset 回执。

Reset是上述处理顺序的唯一特例：服务端必须先查询实例级Reset回执，再校验请求中的旧 `runId`。首次成功Reset创建新 `runId`，并把新轮次安全初始状态固定为 `revision=1`；进程首次启动等同于执行一次内部Reset，也从 `revision=1` 开始。相同Reset命令重放返回首次生成的新 `runId` 和 `appliedRevision=1`。

### 健康与状态

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health` | 进程、Modbus 监听和 Engine 状态。 |
| `GET` | `/snapshot` | 返回全局故障、Modbus 端点及八仓完整状态。 |
| `POST` | `/reset` | 建立新的 `runId`，清除故障和延迟任务，恢复安全空仓状态。 |

Reset请求示例：

```json
{
  "runId": "run-001",
  "commandId": "cmd-reset-001",
  "expectedRevision": 25
}
```

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
| `PUT` | `/slots/{slotNo}/cargo` | `{ "runId": "run-001", "commandId": "cmd-cargo-001", "state": "OCCUPIED", "expectedRevision": 10 }` | 只接受 `EMPTY/OCCUPIED`。 |
| `POST` | `/slots/{slotNo}/close-door` | `{ "runId": "run-001", "commandId": "cmd-close-001", "expectedRevision": 11 }` | 触发既有锁反馈延迟模型。 |

正常联调不提供任意 `open-door` 或 `unlock` HTTP 接口。仓门打开必须来自 HMI 的 Modbus 开锁输出和既有自动弹门模型。

货物状态和关门操作采用确定的无变化语义：

- 仓位已是请求的 `EMPTY/OCCUPIED` 时，新的合法命令返回成功，`changed=false`，`revision` 不递增；
- 仓门已经关闭时，新的 `close-door` 命令返回成功，`changed=false`，`revision` 不递增；
- 只有货物状态确实需要变化且仓门未打开时才返回业务冲突，`reasonCode=DOOR_NOT_OPEN`；
- 已执行命令使用原 `commandId` 重试时，始终按前述幂等规则重放首次结果。

### 传感器故障注入

| 方法 | 路径 | 请求示例 |
| --- | --- | --- |
| `PUT` | `/slots/{slotNo}/lock-feedback-override` | `{ "runId": "run-001", "commandId": "cmd-lock-001", "mode": "AUTO|FIXED_0|FIXED_1", "expectedRevision": 12 }` |
| `PUT` | `/slots/{slotNo}/light-curtain-override` | `{ "runId": "run-001", "commandId": "cmd-light-001", "mode": "AUTO|FIXED_0|FIXED_1", "expectedRevision": 13 }` |

必须区分内部真实物理状态和覆盖后的 DI。覆盖传感器不能偷偷改变货物或仓门事实。

### Modbus 通信故障注入

| 方法 | 路径 | 请求示例 |
| --- | --- | --- |
| `PUT` | `/faults/modbus` | `{ "runId": "run-001", "commandId": "cmd-modbus-fault-001", "mode": "NORMAL|NO_RESPONSE|DISCONNECT|DELAY", "delayMs": 500, "expectedRevision": 14 }` |

- `NO_RESPONSE`：保持 TCP 连接但不返回请求；
- `DISCONNECT`：进入持续故障状态，立即断开全部当前客户端；在显式恢复为 `NORMAL` 前，所有新连接都必须被立即拒绝或接受后立即关闭，不能偶尔放行；
- `DELAY`：对每个合法 Modbus 请求在处理和响应前施加确定性延迟，不使用随机延迟；`delayMs` 范围为1～60000ms，切换模式、Reset或服务停止必须取消尚未完成的延迟；
- `NORMAL`：恢复通信，不修改仓位物理事实。

## 状态机与并发要求

- HTTP、WPF 与 Modbus 的并发访问必须串行化到 Engine 的同一锁或消息队列。
- `reset` 必须取消所有旧 Pulse、锁反馈和光幕反馈延迟任务，旧任务不得污染新 `runId`。
- 同一 `runId + commandId` 且内容相同的重复请求必须幂等重放；相同命令身份但内容不同必须返回 409。
- 所有 API 错误使用稳定 `reasonCode`，不能只返回中文字符串。
- API 日志不得记录凭据；本接口不需要生产凭据，但必须限制 loopback。

### 已确认的 revision 规则

`revision` 表示当前测试轮次中可由快照观察到的控制/物理状态版本。以下变化各自在一次原子提交后递增一次：

- DO原始值、仓门事实或货物事实变化；
- 锁反馈/光幕原始值或其 `pending` 状态变化；
- 传感器覆盖模式变化；
- Modbus故障模式或有效延迟参数变化；
- Reset建立新轮次后的安全初始状态。

同一个原子动作同时改变多个字段时只递增一次；例如一次开锁输入同时改变DO、仓门事实并建立锁反馈pending，视为一个版本。延迟反馈稍后真正落地并清除pending时属于另一个版本。

以下行为不得递增 `revision`：

- GET查询、健康检查和日志写入；
- HTTP或Modbus客户端连接、断开及连接数变化；
- 校验失败、409冲突、404/405请求；
- 幂等重放；
- 新命令把状态设置为已经存在的相同值，或关闭已经关闭的门。

`observedAt`是本次响应生成时间，不代表最近一次状态变化时间。revision不得因生成快照或读取状态而变化。

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
11. 相同 `runId + commandId`、相同内容只执行一次并重放原结果；相同命令身份、不同内容稳定返回409。
12. Reset响应丢失后使用原命令重试，必须返回同一个新 `runId`，不能创建第二轮测试。
13. 设置相同货物状态、相同覆盖模式或关闭已关闭仓门时返回 `changed=false`，revision保持不变。

## 完成条件

- 提交中包含 API OpenAPI 文档或等价机器契约；
- 可一条命令启动同时提供WPF、Modbus和HTTP的模拟器；
- 测试程序能够只通过 HTTP 控制环境，并让真实 HMI 只通过 Modbus 观察结果；
- 全部新旧测试 PASS；
- README 写明统一启动命令、两个监听端口和资格边界；
- 将实现 commit、配置哈希、测试结果和已知限制交给 Onboard 联调负责人。

