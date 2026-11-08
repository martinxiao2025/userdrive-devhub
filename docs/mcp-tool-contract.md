# MCP 工具契约（devhubd）

本文件是 `devhubd` 对 Copilot Chat / Agent 暴露的 **MCP 工具面与资源面**的唯一契约说明。
字段名、枚举值与错误码以代码为准：枚举来自 `src/DevHubd.Mcp/Contracts/ToolVocabulary.cs`，
错误码来自 `src/DevHubd.Core/Models/DevHubErrorCodes.cs` 与各工具内的任务级码。
`tests/DevHubd.Tests/ToolSurfaceSnapshotTests.cs` 对工具名、参数名、枚举词表与关键字段做快照，
任何改动都会先让测试失败，必须同步更新本文件。

- 契约版本：**schema v5**（数据目录 `devhub.db` 的 `user_version`）
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
| id 形态 | 结果 / 队列项 / 问题单的 `id` **一律使用 `result_list` / `queue_list` / `input_request_list` 输出的紧凑形式**（32 位十六进制，无连字符）。带连字符的 GUID、`Queue-`/`Result-` 等前缀形式**不受支持**，传入返回 `UNKNOWN_RESULT` / `UNKNOWN_INPUT_REQUEST` 或空结果 |

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
| 问题单状态（`input_request.*`） | `open` / `answered` / `expired` / `cancelled` / `all`（仅过滤用） |
| 任务审批状态（`devhub://tasks.approvalState`） | `none` / `pending` / `approved` / `rejected` |

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
| `roles[]` | array | 角色快照：`id` / `name` / `delivery` / `maxParallel` / `running`（v5 起） |
| `execution` | object | 执行边界：`modelRuns` / `readRoot` / `writeRoot` / `resultDelivery`（见 §5） |
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
| `force` | bool | false | 调度处于 manual 暂停时强行入队（quota/auth 等硬暂停不可覆盖） |
| `requestId` | string? | null | 幂等键（1–100 字符）；同键重复调用返回首次创建的队列项 |

返回 `{ taskId, workspaceId, status, queueItemId, provider, dryRun }`。
`force` 只覆盖 manual 暂停，不改变“已在队列中”的去重语义；需要去重请使用 `requestId`。

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
`scanRequested` / `changed` / `probeStarted` / `providers[]`。
`probeStarted=true` 表示本次调用启动了后台探测（`action=probe_models`），探测进行中再次调用返回 `false`，
结果通过 `scheduler_status.providers[]` 轮询。

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

### 3.8 `input_request_list`（只读）

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `workspaceId` | string? | null | 按工作区过滤 |
| `taskId` | string? | null | 按任务过滤 |
| `status` | string | `open` | `open/answered/expired/cancelled/all` |
| `limit` | int | 20 | 1–100 |
| `cursor` | string? | null | 上一页 `nextCursor` |

返回 `items[]`：`id/workspaceId/taskId/queueItemId/roleId/status/question/missingInputs/optionCount/createdAt/expiresAt/answeredAt` 与 `nextCursor`。
错误码：`INVALID_STATUS`、`INVALID_LIMIT`、`INVALID_CURSOR`。

### 3.9 `input_request_get`（只读）

参数：`id`（必填）。
返回：`id/workspaceId/taskId/queueItemId/roleId/status/question/context/options[]/missingInputs/allowFreeText/createdAt/expiresAt/answeredAt/answer/answerOptionId/answerSource/answeredBy/related[]`。
`context` 为 JSON 文本（摘要、相对路径、已脱敏片段），`related[]` 为同任务最近 5 条其他问题单。
错误码：`UNKNOWN_INPUT_REQUEST`。

### 3.10 `input_request_answer`

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | 是 | 问题单 id |
| `answer` | string | 是 | 1–4000 字符 |
| `optionId` | string? | 否 | 必须属于该问题单的 `options` |
| `answeredBy` | string? | 否 | 缺省 `mcp`，写入 `answered_by` 与审计 |
| `idempotencyKey` | string? | 否 | 写入审计便于追踪重试；幂等本身由相同答案保证 |

返回：`{ id, status:"answered"|"duplicate", resumedQueueItemId, nextScanAt, acceptedAt }`。
回答在同一事务内清除队列项 `hold_kind/hold_ref` 并置 `available_at=now`，随后请求一次调度唤醒。
错误码：`UNKNOWN_INPUT_REQUEST`、`INPUT_REQUEST_CLOSED`、`INVALID_ANSWER`、`ANSWER_CONFLICT`。
幂等：相同答案重复提交返回首次结果（`status=duplicate`）；不同答案返回 `ANSWER_CONFLICT`，不覆盖。

### 3.11 `input_request_cancel`

参数：`id`、`reason?`。
返回：`{ id, status:"cancelled", queueItemId, cancelledAt }`。
语义：仅 `open` 可撤销；挂起队列项一并终止（`status=cancelled`、`last_error_code=INPUT_REQUEST_CLOSED`），任务不会在缺少输入时继续执行。
错误码：`UNKNOWN_INPUT_REQUEST`、`INPUT_REQUEST_CLOSED`。

### 3.12 `result_approve`

| 参数 | 类型 | 说明 |
|---|---|---|
| `resultId` | string | 结果 id |
| `comment` | string? | 审批意见，写入审计 |

返回：`{ resultId, outcome:"delivered", taskId, decidedAt }`。
语义：仅 `awaiting_review` 可审批；通过后结果终态置 `delivered`，`task_view.approval_state` 置 `approved`，
并写 `audit(action='result.approved', actor='mcp')`。
错误码：`UNKNOWN_RESULT`、`RESULT_NOT_PENDING`（均 retryable=false）。

### 3.13 `result_reject`

参数：`resultId`、`comment?`。
返回：`{ resultId, outcome:"failed", taskId, decidedAt }`。
语义：驳回后结果终态置 `failed`，`task_view.approval_state` 置 `rejected`，写 `audit(action='result.rejected')`
与 `notice.kind='failure'`（去重键 `result-rejected:<resultId>`）。
错误码：`UNKNOWN_RESULT`、`RESULT_NOT_PENDING`。

两个工具同向重复提交是幂等的（`decidedAt=null`，不重复写审计/通知）；反向提交返回
`RESULT_NOT_PENDING`，不覆盖既有终态。审批不修改任务文件。

---

## 4. 资源清单（只读）

| URI | 内容 |
|---|---|
| `devhub://limits` | 预算与并发上限 |
| `devhub://results/unread` | 未读结果列表 |
| `devhub://scheduler/queue` | 队列快照 |
| `devhub://scheduler/status` | 与 `scheduler_status` 同一快照 |
| `devhub://tasks` | 任务物化视图（含 `roleId`、`seenAt`、`approvalState`；`blocked` 表示依赖未满足或角色未声明） |

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
| provider / 桥资源不可用（auth / quota / no_model / bridge_down） | 原状态保持 | 推迟后重试，不消耗重试预算；`error_kind` 为上述四类之一 |
| 队列项认领写入冲突 | 原状态保持 | 该项推迟 30 秒并写 `lastErrorCode=CLAIM_FAILED`，同一 tick 其它项照常执行 |

`roundsExhausted` 只表示“预算用完”，不改变状态；自动化流程应把 `roundsExhausted=true`
视同需要人工复核，不得当成“任务已完成”。

**读 / 写 / 执行边界**（两个档位一处说清，不留隐式差别）：

| 方面 | `auto`（结果 `delivered`） | `dispatch_only`（结果 `awaiting_review`） |
|---|---|---|
| 读 | `read_file` / `list_dir` / `search` 读**工作区根**（只读） | 同左：同样读**工作区根**，可读源码与既有文件 |
| 写 | `write_file` / `apply_patch` 写**工作区根**，且必须落在任务 `writeScope` 内（否则 `SCOPE_DENIED`） | 只写该队列项的**隔离目录** `work/<queueItemId>`；不写工作区，`writeScope` 不参与校验 |
| 执行 | 模型执行回合 | 模型**同样执行回合**，只是产物不出隔离目录 |
| 结果 | 直接 `delivered` | `awaiting_review`，需 `result_approve` / `result_reject` |

- 实现位置：`TurnRunner.RunAsync` 组装 `TurnWorkspace(ReadRoot, WriteRoot, WriteScope, EnforceScope)`，
  读根恒为 `workspace.Root`，写根为 `workDirectory ?? workspace.Root`；`ToolDispatcher` 的读工具用
  `ReadRoot`，写工具用 `WriteRoot`。
- 越界防护：所有路径先经 `PathGuard.ResolveInside` —— 拒绝空路径、绝对路径、`..`、敏感路径
  （`.git` / `.ssh` / `secrets` / `.env*` / `*.pem` / `*.key` / `*.pfx` / `*.p12`）、根外路径与符号链接/junction 逃逸；
  auto 档位额外校验 `writeScope`。验证方式见 `PathGuardTests` 的
  `ToolDispatcher_DispatchOnly_ReadsWorkspaceAndWritesIsolationAsync`（可读工作区文件、写入只落隔离目录、
  `../` 与绝对路径越界读被拒绝、敏感路径被拒绝）。
- 边界差异：`dispatch_only` 下读工具**看不到**隔离目录里的产物（读根是工作区）；模型需要确认产物时
  以回合总结中的相对路径为准，人工从 `work/<queueItemId>` 复核。
- `scheduler_status.execution` 把上述边界显式暴露给调用方：
  `{ modelRuns: true, readRoot: "workspace", writeRoot: "workspace" | "isolation", resultDelivery: "delivered" | "awaiting_review" }`。

**重试预算口径**：预算只看 `attempt` 表中**非 `NEEDS_INPUT` 且非资源类失败**的回合数：

```
COUNT(attempt WHERE queue_item_id = ?
                 AND (error_code IS NULL OR error_code <> 'NEEDS_INPUT')
                 AND (error_kind IS NULL OR error_kind NOT IN ('auth','quota','no_model','bridge_down')))
  < max_attempts
```

才允许认领。`AUTH_FAILED` / `QUOTA_EXHAUSTED` / `NO_MODEL` / `BRIDGE_DOWN` 由 provider / 桥自身不可用导致，
与任务无关，因此和提问挂起一样不消耗预算；`PROVIDER_BLOCKED`（`error_kind=quota`）同样豁免。
`queue_item.attempt` 单调递增、挂起时不回退，因此 `attempt` 字段是“真实回合序号”而不是“已消耗预算数”；
`queue_get.attempts[]` 会按 `attemptNo` 保留每次真实回合（含提问回合、资源类失败回合的 token）。

**恢复回合的答案注入**：认领恢复时，`TurnRunner` 读取同一 `queue_item` 下所有 `status='answered'` 的问题单，
按提问时间升序追加到任务指令之后：

```
## 此前的人工回答

系统已把你在之前回合提出的问题与用户回答附在下面（按提问时间排序）。请直接采用这些回答继续执行，不要就已回答的问题再次调用 ask_input。

### 回答 1（提问于 2026-11-08T11:40:15.000Z）
问题：写入哪个目录？
选项：src（推荐）；docs
用户回答：src（选中选项：src）
回答时间：2026-11-08T11:40:37.129Z（e2e-operator）
```

截断策略：单条问题保留前 500 字符、单条回答保留前 1000 字符，超出追加 `…（截断）`；
整个回答段上限 8000 字符，超限时从最早的回答开始丢弃，并在段首标注「更早的 N 条回答因长度限制已省略」。
已取消 / 已过期的问题单不注入。

---

## 6. 变更记录

### 读写边界修正 — 2026-11-08（dispatch_only 读放开、写仍受控）

- **读放开**：`TurnWorkspace` 恢复 `ReadRoot` / `WriteRoot` 双根。读根恒为工作区根，
  `read_file` / `list_dir` / `search` 在 `dispatch_only` 下也能读源码与既有文件；路径校验不放松，
  越界（`..`、绝对路径、符号链接逃逸）与敏感路径照旧拒绝。
- **写仍受控**：写根在 `dispatch_only` 为该队列项的隔离目录 `work/<queueItemId>`，auto 为工作区根并受
  `writeScope` 约束。`write_file` / `apply_patch` 不会写工作区。
- **语义显式化**：`dispatch_only` 的本意是“产物不直接落工作区、结果待人工复核”，不是“模型不动手”；
  模型仍执行回合。该边界写入 `scheduler_status.execution`（`modelRuns` / `readRoot` / `writeRoot` /
  `resultDelivery`）与系统提示词，不再靠工具可见范围暗示。
- **一处说清**：契约 §5「读 / 写 / 执行边界」表格列出两个档位在读、写、执行、结果四方面的确切边界。
- 测试：`ToolDispatcher_DispatchOnly_ReadsWorkspaceAndWritesIsolationAsync` 覆盖可读工作区文件、
  写入只落隔离目录、`../` 与绝对路径越界读被拒绝、敏感路径被拒绝；auto 档位沿用 `SCOPE_DENIED` 用例。

### B 变体补充 — 2026-11-08（答案注入恢复回合、资源类豁免预算）

- **答案注入**：`TurnRunner` 在每次回合开始读取该队列项已回答的问题单
  （`InputRequestStore.ListAnsweredForQueueItemAsync`），按提问时间升序以「## 此前的人工回答」
  段落追加到任务指令后；问题 500 字符、回答 1000 字符单条截断，整段 8000 字符上限、超出丢弃最早回答。
  实现位置：`TurnRunner.BuildPromptWithAnswers` / `FormatAnsweredInput` / `TruncateAnswerText`。
  设计文档 §3.5 已同步。
- **资源类失败豁免重试预算**：`ModelErrorKinds.BudgetExempt`（auth / quota / no_model / bridge_down）
  同时用于认领 SQL 与 `ExecutionStore.CountConsumingAttemptsAsync`，与旧文档口径一致。
  上一版变更记录中「资源类失败现在也消耗预算」的描述作废，以本条为准。
- ~~**读写根统一**：`TurnWorkspace` 收敛为单一 `Root`~~ —— 已作废，改为上方「读写边界修正」
  （读根=工作区、写根=隔离目录，双根）。
- 测试：`Pump_AfterAnswer_ResumesWithNewAttemptAndProducesResultAsync` 断言恢复回合 prompt 含答案；
  资源失败用例连续 4 个 auth 回合仍可认领（`max_attempts=3` 不被闸门挡死）。

### B 变体修复 — 2026-11-08（attempt 单调、预算改按回合数、tick 单项隔离）

- **attempt 只增不减**：删除挂起时对 `queue_item.attempt` 的回退。每次真实认领都会递增并插入
  独立的 `attempt.attempt_no`，提问回合（`outcome=abandoned`、`error_kind=needs_input`）的 token
  与工具调用次数一并保留，`queue_get.attempts[]` 可看到完整回合序列（如 `1,2`）。
  修复了「回答后重新认领插入重复 `attempt_no` 触发 `UNIQUE(queue_item_id, attempt_no)`」的阻塞缺陷。
- **重试预算改为按非 `NEEDS_INPUT` 回合数判定**：认领 SQL 的预算谓词与
  `ExecutionStore.CountConsumingAttemptsAsync` 共用同一口径（见 §5），不再依赖回退计数器。
  资源类失败是否豁免见上方「B 变体补充」（现已按 `error_kind` 豁免）。
- **单个队列项失败不中断 tick**：认领写入冲突抛 `QueueItemClaimException` 后回滚事务，该项
  `reschedule(now+30s, CLAIM_FAILED)` 并继续同批其它项；泵级异常由调度后台服务兜住，本轮 tick
  正常结束、不触发 5 秒整轮退避。新增错误码 `CLAIM_FAILED`。
- **id 契约收紧**：result / queue / input_request 的 `id` 只接受列表工具输出的紧凑形式，
  带连字符形式不支持（见 §1.3）。

### U12 — 2026-11-08（无状态整改：档位以库为准、派发幂等、探测异步、重启兜底）

- 交付档位统一以 `scheduler_state.mode` 为唯一事实来源：`TurnRunner` 每回合读库，
  `devhub://limits` 与控制面快照同源读取；`EffectiveDeliveryMode` 仅保留为启动种子/展示缓存，
  不再参与交付判定，消除“库已切换、执行仍按启动配置跑”的漂移。
- `dispatch_now` 新增可选 `requestId`（1–100 字符）：提供时以 `request:{workspaceId}:{requestId}`
  作为 `dedupe_key`，同键重复派发返回首次创建的队列项（`status=deduped`、`queueItemId` 相同）。
- `scheduler_control` 的 `probe_models` 改为异步：调用只启动后台探测并立即返回 `probeStarted=true`，
  provider 结果由 `scheduler_status` 轮询；探测进行中重复调用返回 `false`，不再阻塞调用方。
- 启动时（单实例互斥下）把 `ended_at IS NULL` 的遗留 attempt 统一置 `abandoned/PROCESS_RESTART`，
  避免重启后统计与排障仍显示“运行中”。新增错误码 `PROCESS_RESTART`。

### U10 — 2026-11-08（不确定项回流：会话侧取回与回答）

- 工具面新增 4 个问题单工具（共 12 个）：`input_request_list` / `input_request_get` /
  `input_request_answer` / `input_request_cancel`，参数与返回见 §3.8–§3.11。
- `input_request_answer` 在**同一事务**内写入答案并清除队列项 `hold_kind/hold_ref`、置 `available_at=now`，
  提交后请求一次调度唤醒；`nextScanAt` 取自调度状态存储，供调用方判断何时可见重跑。
- 幂等：相同答案重复提交返回 `status=duplicate` 与首次结果；不同答案返回 `ANSWER_CONFLICT`，不覆盖已答内容。
- `idempotencyKey` 没有独立列，仅写入 `audit.detail`（`idem=`）便于追踪重试，**不参与幂等判定**。
- `input_request_cancel` 终止挂起队列项（`status=cancelled`、`last_error_code=INPUT_REQUEST_CLOSED`）；
  人工撤销即放弃该次任务，同内容不会自动重新入队（直到任务文件内容变化）。
- 每次 tick 的过期扫描把超时 `open` 置 `expired`，队列项置 `failed` 并写 `last_error_code=INPUT_TIMEOUT`，
  同时写 `notice.kind='failure'`（标题以「需要输入超时」开头）。
- `input_request_list` 默认 `status=open`、`limit=20`；`status=all` 表示不过滤。非法 `status`/`limit`/`cursor`
  分别返回 `INVALID_STATUS`/`INVALID_LIMIT`/`INVALID_CURSOR`。

### U9 — 2026-11-08（不确定项回流：提问即挂起）

- 模型侧工具面新增 `ask_input`：仅当角色 `canRequestInput=true` 时暴露；每回合最多提问一次，第二次调用返回既有 `requestId` 提示而不重复落库。
- 提问写入 `input_request`：`dedupe_key = workspace/task/task_hash/question_hash`，同一任务只保留一条 `open`；重复提问复用既有 id。
- 提问回合记 `attempt.outcome='abandoned'`、`error_kind='needs_input'`，token 与工具调用次数照实保留；`queue_item` 回到 `pending` 并写 `hold_kind='input'`/`hold_ref`。**（本条当时的「attempt 回退」实现已被上方 B 变体修复取代：attempt 只增不减，预算改按非 `NEEDS_INPUT` 回合数判定。）**
- 认领谓词追加 `hold_kind IS NULL`：挂起项不占并发槽、不参与自动重试，重启后保持挂起。
- 每次 tick 在入队前取消"任务内容已变更"的旧 `open` 问题单，并终止其挂起队列项（`last_error_code=TASK_CHANGED`），避免旧答案答到新任务。
- 挂起时写 `notice.kind='failure'`（标题以「需要输入」开头）提示会话侧取回；`devhub://limits` 的工具白名单包含 `ask_input`。

### U8 — 2026-11-08（任务模型扩展与 `task_upsert`）

- 任务文件新增可选 `role`（默认 `implementer`）、`dependsOn`、`requiresApproval`、`source`；封闭字段同步到 `schemas/task.schema.json`。
- `task_view` 物化 `role_id`/`depends_on`：依赖未全部 `done` 或角色未在 `role_view` 声明时置 `blocked`，不静默降级。
- 入队时把任务角色复制到 `queue_item.role_id`；`devhub://tasks` 条目新增 `roleId`。
- 新工具 `task_upsert` 上线（参数与错误见 §3.3 末尾），新增错误码 `AMBIGUOUS_WORKSPACE`。
- `role_view` 为空（未部署插件包）时按 v4 legacy 行为放行：不做角色闸门，队列角色记为 `implementer`。

### v5 — 2026-11-08（U7：角色快照与回流结构，纯增量）

- `task_view` 新增 `role_id`（`NOT NULL DEFAULT 'implementer'`）、`depends_on`、`approval_state`（`none/pending/approved/rejected`，默认 `none`）。
- `queue_item` 新增 `role_id`、`hold_kind`（`input/approval`）、`hold_ref`，并新增索引 `idx_queue_role`。
- 新增 `role_view`：角色声明（`plugin/roles/*.json`）的数据库快照，含 `content_hash` 与全部预算/并发字段。
- 新增 `input_request`：问题单。`dedupe_key` 使用**部分唯一索引**只约束 `status='open'`，历史 `expired/cancelled` 记录不阻塞同题重问。
- `scheduler_status` 新增只读字段 `roles[]`（`id/name/delivery/maxParallel/running`）；`running` 为 `claimed/running` 队列项的实时计数。
- 启动时若找到 `plugin/plugin.json`（或 `DEVHUB_PLUGIN_DIR`），角色声明必须全部通过校验，否则以退出码 **6** 终止并逐条打印文件与字段名。

### U11 — 2026-11-08（审批闸门：结果通过/驳回）

- 工具面新增 `result_approve` / `result_reject`（共 14 个），参数与语义见 §3.12–§3.13。
  两个工具同向重复提交幂等（`decidedAt=null`、不重复写审计/通知），反向提交返回 `RESULT_NOT_PENDING`。
- 交付判定收紧：`task.requiresApproval=true` 时，即便全局档位与任务档位都是 `auto` 也进入
  `awaiting_review`；任务级声明不能让全局 `auto` 绕过人工闸门。
- 结果落库为 `awaiting_review` 时把 `task_view.approval_state` 置 `pending`；交付成功（`delivered`）时
  清回 `none`，避免驳回后的旧状态残留。审批通过/驳回分别置 `approved`/`rejected`。
- 审批写 `audit`：`result.approved` / `result.rejected`，`actor` 固定 `mcp`（契约暂未开放调用方声明）。
  `comment` 截断到 200 字符写入 `audit.detail`。
- 驳回额外写 `notice.kind='failure'`，去重键 `result-rejected:<resultId>`，正文优先取驳回原因，否则取结果摘要；
  通过不写通知（结果本身已由 `result_list` 可见）。
- `devhub://tasks` 条目新增 `approvalState`；审批**不修改任务文件**，任务清单仍由工作区维护。
- 新增错误码（只增不改）：`UNKNOWN_RESULT`、`RESULT_NOT_PENDING`；连同前序单元累计新增
  `NEEDS_INPUT`、`INPUT_TIMEOUT`、`INPUT_NOT_ALLOWED`、`UNKNOWN_INPUT_REQUEST`、`INPUT_REQUEST_CLOSED`、
  `INVALID_ANSWER`、`ANSWER_CONFLICT`、`INVALID_ROLE`、`TASK_CONFLICT`、`TASK_CHANGED`、
  `DEPENDENCY_UNMET`、`AMBIGUOUS_WORKSPACE`。

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
