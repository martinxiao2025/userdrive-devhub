# MCP 工具契约（devhubd）

本文件是 `devhubd` 对 Copilot Chat / Agent 暴露的 **MCP 工具面与资源面**的唯一契约说明。
字段名、枚举值与错误码以代码为准：枚举来自 `src/DevHubd.Mcp/Contracts/ToolVocabulary.cs`，
错误码来自 `src/DevHubd.Core/Models/DevHubErrorCodes.cs` 与各工具内的任务级码。
`tests/DevHubd.Tests/ToolSurfaceSnapshotTests.cs` 对工具名、参数名、枚举词表与关键字段做快照，
任何改动都会先让测试失败，必须同步更新本文件。

- 契约版本：**schema v4**（数据目录 `devhub.db` 的 `user_version`）
- 更新时间：2026-11-08

---

## 1. 返回形态

### 1.1 成功

工具返回的 `content` 是**两个文本块**：

1. 正文：紧凑 JSON（camelCase），结构见第 3 节；
2. 尾部摘要：以 `— devhub · ` 开头，≤ 240 字符，供 Chat 侧快速浏览。

资源（`resources/read`）只返回正文 JSON，没有摘要块。

### 1.2 失败

失败一律 `isError=true`，正文是统一信封（`McpErrors.Envelope`）：

```json
{
  "code": "INVALID_ARGUMENT",
  "message": "可诊断的中文说明",
  "details": { },
  "retryable": true,
  "next": "下一步动作提示"
}
```

- `code`：大写下划线，稳定不改；与模型侧 `DevHubErrorCodes` 共用词表。
- `message`：不含凭据、不含文件内容。
- `retryable`：调用方是否值得原地重试（参数错误为 true，状态冲突通常为 false）。
- `next`：给模型的可执行提示；无提示时为 `null`。

### 1.3 通用约定

| 约定 | 说明 |
|---|---|
| 命名 | 一律 camelCase；错误码与库内列名除外 |
| 布尔 | 真布尔，不用 `"true"` 字符串 |
| 缺失值 | `null` 字段不序列化（`ToolJson.Options` 忽略 null） |
| 时间 | **UTC + `Z`**，固定 7 位小数：`2026-11-08T04:09:03.1234567Z` |
| 分页 | `limit` 默认 20（`result_list` 为 10）、上限 100；`nextCursor` 为不透明字符串，`null` 表示到底 |
| 只读 | `scheduler_status` / `queue_list` / `queue_get` / `result_list` 无副作用 |

---

## 2. 枚举词表

| 词表 | 取值 |
|---|---|
| 队列状态（`queue_list.status`） | `pending` / `claimed` / `running` / `succeeded` / `failed` / `dead` / `cancelled` / `all` |
| 交付档位（`scheduler_control.mode`、`scheduler_status.mode`） | `auto` / `dispatch_only` |
| 调度动作（`scheduler_control.action`） | `start` / `pause` / `resume` / `scan_now` / `set_mode` / `reset_breaker` / `probe_models` / `status` |
| 手动派发 provider（`dispatch_now.provider`） | `auto` / `openai` / `vscode` |
| provider 熔断状态 | `closed` / `open` / `half_open` |
| 结果终态（`result.outcome`） | `delivered` / `awaiting_review` / `failed` |
| 任务状态（`devhub://tasks`） | `ready` / `blocked` / `done` / `cancelled` / `missing` |

---

## 3. 工具清单

### 3.1 `scheduler_status`（只读）

- 参数：**无**（B2 起删除 `verbose`）
- 返回：调度器快照。

| 字段 | 类型 | 说明 |
|---|---|---|
| `enabled` | bool | 调度是否启用 |
| `mode` | string | `auto` / `dispatch_only` |
| `phase` | string | `idle` / `scanning` / `paused` 等 |
| `pauseReason` | string? | 暂停原因 |
| `intervalSeconds` | int | 扫描间隔 |
| `lastScanAt` / `nextScanAt` | string? | UTC + Z |
| `counters` | object | 扫描/入队/去重计数 |
| `queue` | object | 按状态聚合的队列计数 |
| `providers[]` | array | `provider` / `state` / `detail` / `checkedAt`（UTC + Z） |
| `tokens` | object | 累计用量；`attemptsWithUsage` / `attemptsWithoutUsage` / `estimatedAttempts` 分列 |
| `mcp` | object | `host` / `port` / `path`；端口固定 3091 |
| `host` | object | `version` / `pid` / `schemaVersion` / `dataDirectory` / `startedAt`（UTC + Z） |
| `unread` | object | 未读结果计数 |

错误码：`SCHEDULER_UNAVAILABLE`（数据库不可用，retryable=true）

### 3.2 `queue_list`（只读）

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `status` | string? | `all` | 见 §2 队列状态 |
| `limit` | int | 20 | 1–100 |
| `cursor` | string? | null | 上一页返回的 `nextCursor` |

返回 `{ items: QueueItemSummary[], nextCursor }`；`QueueItemSummary` 字段：
`id` / `workspaceId` / `taskId` / `status` / `priority` / `attempt` / `maxAttempts` /
`leaseOwner` / `leaseExpiresAt` / `lastErrorCode` / `createdAt` / `updatedAt`（时间均为 UTC + Z）。

错误码：`INVALID_STATUS`、`INVALID_LIMIT`（retryable=true）

### 3.3 `queue_get`（只读）

| 参数 | 类型 | 说明 |
|---|---|---|
| `queueItemId` | string | 队列项 id |

返回 `{ item, taskTitle, attempts[] }`。`attempts[]` 字段：
`attemptNo` / `provider` / `model` / `startedAt` / `endedAt` / `outcome` / `errorCode` /
`toolCalls` / `promptTokens` / `completionTokens` / `totalTokens` /
`usageEstimated` / `usageAvailable` / `roundsExhausted`。

- `usageAvailable=false`：provider 未返回用量，三个 token 字段均为 `null`（**不是 0**）。
- `usageEstimated=true`：数值按字符数估算，不是真实 token。
- `roundsExhausted=true`：本回合耗尽工具轮次预算，正文是框架移除工具后的兜底答复（见 §5）。

错误码：`UNKNOWN_QUEUE_ITEM`（retryable=false）

### 3.4 `dispatch_now`

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `taskId` | string | 必填 | 任务 id |
| `workspaceId` | string? | null | 任务 id 跨工作区重名时必须提供 |
| `provider` | string | `auto` | `auto` / `openai` / `vscode` |
| `dryRun` | bool | false | 只校验不派发 |
| `force` | bool | false | 跳过“已在队列中”的去重 |

返回 `{ taskId, workspaceId, status, queueItemId, provider, dryRun }`。

错误码：`UNKNOWN_TASK`、`AMBIGUOUS_TASK`、`TASK_NOT_READY`、`SCHEDULER_PAUSED`、
`PROVIDER_UNAVAILABLE`、`NO_MODEL_PROVIDER`、`INVALID_PROVIDER`。

### 3.5 `scheduler_control`

| 参数 | 类型 | 说明 |
|---|---|---|
| `action` | string | 见 §2 调度动作 |
| `reason` | string? | 审计原因 |
| `mode` | string? | `set_mode` 必填：`auto` / `dispatch_only` |
| `provider` | string? | `reset_breaker` 必填：`openai` / `vscode` |

返回 `SchedulerControlSnapshot`：`enabled` / `mode` / `pauseReason` / `intervalSeconds` /
`scanRequested` / `changed` / `providers[]`。

错误码：`INVALID_ACTION`、`INVALID_ARGUMENT` 及 `SchedulerControlException` 携带的任务级码（retryable 由异常决定）。

### 3.6 `result_list`（只读）

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `unreadOnly` | bool | false | 只看未读 |
| `limit` | int | 10 | 1–100 |
| `cursor` | string? | null | 上一页返回的 `nextCursor` |

返回 `{ items: ResultSummary[], nextCursor }`；`ResultSummary` 字段：
`id` / `taskId` / `queueItemId` / `provider` / `outcome` / `summary` / `artifacts[]` /
`createdAt` / `readAt` / `toolCalls` / `promptTokens` / `completionTokens` / `totalTokens` /
`usageEstimated` / `usageAvailable` / `roundsExhausted`。

token 与 `roundsExhausted` 取该队列项**最近一次 attempt** 的值，语义同 §3.3。

错误码：`INVALID_LIMIT`（retryable=true）

### 3.7 `result_ack`

| 参数 | 类型 | 说明 |
|---|---|---|
| `ids` | string[]? | 要标记为已读的结果 id |
| `all` | bool | true 时标记全部未读 |

返回 `{ acknowledged: int }`。

错误码：`INVALID_ARGUMENT`（既无 ids 也无 all=true，retryable=true）

---

## 4. 资源清单（只读）

| URI | 内容 |
|---|---|
| `devhub://limits` | 预算与并发上限 |
| `devhub://results/unread` | 未读结果列表 |
| `devhub://scheduler/queue` | 队列快照 |
| `devhub://scheduler/status` | 与 `scheduler_status` 同一快照 |
| `devhub://tasks` | 任务物化视图（含 `seenAt`） |

资源与同名工具使用同一序列化配置，时间同为 UTC + Z。

---

## 5. 执行语义（与 `roundsExhausted` / `usageAvailable` 相关）

| 场景 | 结果状态 | 可观察字段 |
|---|---|---|
| 模型自然完成 | `succeeded` | `roundsExhausted=false` |
| 工具轮次预算耗尽 | `succeeded` | `roundsExhausted=true`，正文是框架移除工具后的兜底答复 |
| 连续 3 次工具错误 | `failed` | `errorCode` 为工具自身稳定码（如 `FILE_NOT_FOUND`），`errorKind=tool_error` |
| 回合超时/停机取消 | `cancelled` | attempt `outcome=cancelled` |
| provider 未返回 usage | 任意 | `usageAvailable=false`，token 三列为 `null` |

`roundsExhausted` 只表示“预算用完”，不改变状态；自动化流程应把 `roundsExhausted=true`
视同需要人工复核，不得当成“任务已完成”。

---

## 6. 变更记录

### v4 — 2026-11-08（数据修正，无字段变化）

**回填旧行 `usage_available`**

- 背景：v3 给既有 `attempt` 行统一写 `usage_available=0`，但更早的 BYOK 行已经记录了真实 token；
  此时 0 会被误读成“provider 没返回用量”，与上文“`usageAvailable=false` ⇔ token 为 `null`”冲突。
- 处理：`input_tokens` / `output_tokens` 任一非空的旧行补标为 1；token 为 `NULL` 的行保持 0。
- 幂等：只更新仍为 0 的行；启动时自动把 `user_version` 从 3 升到 4。
- 影响：`queue_get.attempts[]` / `result_list.items[]` 中，历史 BYOK 行会从 `usageAvailable=false`
  变为 `true`，token 数值不变；历史 `vscode` 行（token 为 `null`）保持 `false`。

### v3 — 2026-11-08（破坏性 + 行为变化）

**B1 时间格式统一 UTC + `Z`**

- 旧：`2026-11-08T04:09:03.1234567+00:00`
- 新：`2026-11-08T04:09:03.1234567Z`
- 影响：所有工具与资源的 `createdAt` / `updatedAt` / `startedAt` / `endedAt` / `readAt` /
  `seenAt` / `lastScanAt` / `nextScanAt` / `checkedAt` / `startedAt`。
- 迁移：库内历史行不改写，读侧统一归一后出契约；解析方若用严格字符串比较，需改为时间解析。
  游标（`nextCursor`）不受影响，仍按库内 `created_at` 排序。

**B2 删除 `scheduler_status.verbose`**

- 旧：`{"verbose": false}` 可选参数（无实际作用）。
- 新：工具无参数。SDK 会忽略多余参数，旧调用**不会报错**，但该参数已无任何效果；
  建议从请求中删除，以免误以为它会改变返回内容。

**新增 `usageAvailable`**

- 位置：`queue_get.attempts[]`、`result_list.items[]`。
- 语义：`false` 表示用量不可得（token 为 `null`），`true` 表示 provider 明确返回了用量。
- 用途：不再需要靠“token 是否为 0”猜缺失。

**新增 `roundsExhausted`**

- 位置：`queue_get.attempts[]`、`result_list.items[]`。
- 语义：`true` 表示工具轮次预算耗尽，正文是移除工具后的兜底答复；状态仍为 `succeeded`。
- 背景：旧实现返回 `TOOL_ROUNDS_EXCEEDED` 错误；改用 `Microsoft.Extensions.AI` 后，
  框架在耗尽时移除工具再调用一轮并正常返回。为保留“不伪造完成”的语义，
  执行器改为**不改写返回路径 + 显式留痕**，并在日志中输出 `roundsExhausted`。

**U3 行为变化**

1. 桥（`vscode`）路径取消：`BridgeChatClient` 原样抛出 `OperationCanceledException`
   （先以 3 秒独立超时发送 `DELETE /turns/{id}` 收尾），由 `TurnRunner` 统一映射为
   `cancelled`；对外可观察结果与旧版一致，但不再包装成自研错误码。
2. 连续工具错误达到 3 次：回合失败，`errorKind=tool_error`，`errorCode` 取工具自身稳定码
   （旧版可能表现为通用回合异常）。

**数据库**

- `attempt` 新增 `usage_available`、`rounds_exhausted`（`INTEGER NOT NULL DEFAULT 0`）。
- 启动时自动从 v2 迁移到 v3，无需手工操作；旧行先统一为 0，随后由 v4 把已有真实 token 的行回填为 1。

### v2 — 2026-10

- `attempt.usage_estimated` 落库，区分真实 token 与估算值。
