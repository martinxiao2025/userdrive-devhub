# 第六轮：取消链路闭环修复计划

## Context

第五轮已验证 v7 重建、`supersede` SQL 边界与认领兜底成立，但取消链路仍未闭环：

- 心跳续租失败只取消心跳自身，回合仍用原始 token 继续跑（继续消耗 provider 预算、可能继续写工作区）；
- 回合结束后直接 `return`，attempt 保持 `ended_at=NULL`，只能等下次启动被 `AbandonOrphanAttemptsAsync` 记成 `PROCESS_RESTART`；
- `SettleAsync` 成功路径无条件写 `succeeded` + 插入结果，决策发生在"最后一次心跳成功之后、回合结束之前"的窗口时，会把 `cancelled` 改回 `succeeded` 并写出第二个结果；
- 契约文档状态值与实现存在两套说法；
- supersede 取消 `hold_kind='input'` 项时未清 hold、未关闭问题单；
- 极简库谎报 `user_version=6` 且缺 `rerun_pending` 时 V7 归一化直接报 SQL 错误，提示不明确。

目标：取消真正传到回合、落库前复核所有权、文档与实现一致、挂起项随取消收尾、缺列给出明确错误。基线 HEAD 为 `ff6279e`。

## 关键决策与约束

1. **attempt 终态用 `abandoned` 承载"取消"**：`attempt.outcome` 的 CHECK 只允许 `succeeded/failed/abandoned`。本轮不改 schema 版本（保持 v7），因此取消档统一写 `outcome='abandoned'`、`error_kind='lease_lost'`、`error_code='SUPERSEDED_BY_DECISION'`，语义与停机取消（`abandoned + SHUTDOWN`）一致。若要求字面 `cancelled`，需要 v8 迁移，超出本轮范围。
2. **租约丢失时只写本进程独占的 attempt 行**：队列项已不属于自己，绝不回写队列状态、不写 result、不动审批态与通知；attempt 行由本进程创建且唯一持有，安全可写。
3. **所有权闸门必须是原子条件更新**：`TrySetSucceededAsync` 用一条 `UPDATE ... WHERE status IN ('claimed','running') AND lease_owner=@self` 判断并落终态，避免"先查后写"的残留竞态。
4. **测试时序必须可观测，不靠猜**：以队列项 `lease_expires_at` 在认领后是否被推进作为"至少一次心跳成功"的证据，再触发决策；避免固定 sleep 造成偶发。

## 改动清单

### 1. 取消传到模型回合（`src/DevHubd.Core/Execution/ExecutionCoordinator.cs`）

- `ExecuteItemAsync`：
  - 新增 `using var roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, heartbeatStop.Token);`
  - `_runner.RunAsync(item, task, workspace, roundCancellation.Token)`（不再传原始 token）。
  - `RunAsync` 外层补 `catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested)`：直接返回，防止回合以异常形式冒泡到 `RunTrackedAsync` 的通用错误分支。
  - `heartbeat` 返回 `false` 时直接返回；attempt 终态已由心跳循环在丢失瞬间写入，回合产出一律丢弃。
- `HeartbeatLoopAsync`：
  - 参数改为 `ClaimedQueueItem claimed`（需要 `AttemptId`），内部继续用 `heartbeatStop` + 执行 token 的 linked 源。
  - 续租返回 `false` 时：先 `CancelSupersededAttemptAsync(claimed, outcome: null)`，再 `stop.CancelAsync()`，返回 `false`。
  - 修正第 297 行与实现不符的注释：明确"续租失败会立即取消回合，并把 attempt 落为 `abandoned + SUPERSEDED_BY_DECISION`"。
- 新增私有方法 `CancelSupersededAttemptAsync(ClaimedQueueItem claimed, TurnRunOutcome? outcome)`：
  - `_store.CompleteAttemptAsync(..., new AttemptCompletion("abandoned", outcome?.Model, ModelErrorKinds.LeaseLost, DevHubErrorCodes.SupersededByDecision, ...))`；
  - 有 `outcome` 时保留工具调用次数与 token 用量，没有则记 0/null；
  - 打 `LogWarning`，字段含 `queueItemId`、`attempt`、`errorCode`。

### 2. 落库前复核状态与租约

- `src/DevHubd.Core/Execution/ExecutionStore.cs` 新增：
  ```csharp
  public async Task<bool> TrySetSucceededAsync(string queueItemId, string ownerId, string now, CancellationToken cancellationToken = default)
  ```
  - `ExecuteUpdateAsync` 条件：`Id == queueItemId && LeaseOwner == ownerId && (Status == "claimed" || Status == "running")`；
  - 置 `status='succeeded'`、清租约与错误字段、写 `updated_at`；
  - 返回受影响行数是否等于 1。
- `ExecutionCoordinator.SettleAsync` 成功分支：
  - 先调用 `TrySetSucceededAsync(item.Id, _ownerId, now, ...)`；
  - 返回 `false`：调用 `CancelSupersededAttemptAsync(claimed, outcome)`、打警告、直接返回；不写 result、不改审批态、不记 provider 成功、不写通知；
  - 返回 `true` 才继续原有成功落库流程（attempt succeeded → result → 审批态 → 通知）。
- 边界：闸门通过后若 attempt/result 写入抛异常，由 `RunTrackedAsync` 记录上下文；队列项已是 succeeded，不会产生第二个结果（result 由唯一队列项串行写入）。

### 3. 契约文档对齐（`docs/mcp-tool-contract.md`）

以实际实现为准：

- §2 `dispatch_now.status` 词表去掉 `pending_review`，保留 `enqueued / deduped / active_exists / delivered_exists / would_enqueue`；补一句"待审拒绝以错误码 `RESULT_AWAITING_REVIEW` 返回，不作为成功 status"。
- §3.4 状态说明改为：
  - `enqueued`（已入队；核心层 `ManualEnqueueResult.Status` 为 `pending`，MCP 对外统一 `enqueued`）；
  - `deduped`（同一 `requestId` 幂等命中，仅表示同 `dedupe_key` 已入队）；
  - `delivered_exists`（同内容已交付，唯一承载"同内容已交付"语义）；
  - `active_exists` / `would_enqueue` 不变。
- §6 第五轮记录里 `pending_review` 从"成功状态词表"移除，改为错误码说明。
- `DispatchStatusText.PendingReview` 分支保留（内部状态枚举仍用于错误分支），不做无意义删除。

### 4. supersede 清理输入挂起（`src/DevHubd.Core/Results/ResultDecisionStore.cs`）

在决策事务内、执行 supersede 更新前：

- 先查出将被取消的队列项 id 集合（同 workspace + task、排除本次结果所属项、`status IN ('pending','claimed','running')`）。
- `UPDATE queue_item` 增加 `hold_kind = NULL`、`hold_ref = NULL`。
- 若集合非空，再执行：
  ```sql
  UPDATE input_request
  SET status = 'cancelled',
      answered_at = COALESCE(answered_at, @now)
  WHERE queue_item_id IN (...)
    AND status = 'open';
  ```
  （该表没有独立关闭时间列，`answered_at` 是唯一可落地的时间戳，沿用"关闭时间写该列"的口径，并加注释说明。）
- 现有 `queue.superseded` 审计与取消计数保留。

### 5. 极简库缺列明确报错（`src/DevHubd.Core/Data/Migrator.cs`）

V7 分支中 `rerunType is null` 时抛：

```csharp
throw new InvalidOperationException(
    "无法升级到 schema v7：task_view 缺少 rerun_pending 列，但 user_version 已声明为 6。" +
    "该库不是受支持的 v6 形态（版本号被谎报或表被手工改动）；请从备份恢复，或删除该库让 devhubd 重建。");
```

真实 v2/v5/v6 库不受影响，不做兼容。

## 测试（沿用 worktree 对照法）

> 基线：`git worktree add <temp> HEAD`（`ff6279e`），把新增/修改的测试文件复制进去后运行
> `dotnet test <worktree>/tests/DevHubd.Tests/DevHubd.Tests.csproj -- --filter-class <类名>`。
> 新测试只使用基线已有 API，可直接编译。`ExecutionOptions.Normalize` 把 `HeartbeatSeconds` 下限夹在 5 秒。

### 1) `P2ExecutionTests.Pump_WhenLeaseLost_CancelsTurnAndClosesAttemptAsync`

- 正常入队后启动 Pump（不 await）；等队列项 `running`。
- 直接把 `lease_owner` 改成 `thief` 模拟租约易主（决策 supersede 的效果等价）。
- FakeModelClient 用 `Task.Delay(7s, token)`，取消时抛 `OperationCanceledException`。
- 用**不抛异常**的轮询最多 6.5 秒观察 `attempt.ended_at`；随后 await Pump 完成，再断言：
  - `attempt.outcome='abandoned'`、`error_kind='lease_lost'`、`error_code='SUPERSEDED_BY_DECISION'`、`ended_at` 非空；
  - `result` 行数为 0；
  - 队列项仍为 `running`（租约已不属于本进程，不得改写）。
- 修复前表现：满 6.5 秒 `ended_at` 仍为 NULL，断言失败；等待结束后旧代码的回合会在 7 秒正常返回，便于清理。

### 2) `P2ExecutionTests.Settle_WhenQueueItemSupersededBeforeTurnEnds_DiscardsResultAsync`

- 入队后额外插入一条 `succeeded` 队列项 `Q0` 与一条 `awaiting_review` 结果 `res-1`（同 workspace + task）。
- 读取目标队列项认领后的 `lease_expires_at`，FakeModelClient 进入后置位"已开始"信号并阻塞在 `TaskCompletionSource`。
- 轮询直到 `lease_expires_at` 被推进（证明至少一次心跳成功且租约仍归自己），再调用
  `new ResultDecisionStore(_factory).DecideAsync("res-1", approve: true, ...)`，随后释放 TCS。
- 用 `try/finally` 保证 TCS 一定释放；await Pump 与 `ActiveCount == 0` 后断言：
  - 被取消的队列项 `status='cancelled'`、`last_error_code='SUPERSEDED_BY_DECISION'`；
  - `result` 总数仍为 1（只有 `res-1`，没有第二个结果）；
  - 该 attempt `outcome='abandoned'`、`error_code='SUPERSEDED_BY_DECISION'`。
- 修复前表现：队列项被改回 `succeeded` 且 `result` 变成 2 行。

### 3) `SchedulerPipelineTests.Decide_CancelsInputHoldAndClosesRequestAsync`（第 4 项）

- `TickAsync` 建队列项后，插一条 `succeeded` 的 `Q0` + `awaiting_review` 结果；把目标队列项置
  `hold_kind='input'`、`hold_ref='IR-1'`；插入 `input_request`（`status='open'`，必填列齐）。
- `DecideAsync(approve: true)` 后断言：
  - 队列项 `status='cancelled'`、`hold_kind/hold_ref IS NULL`、`last_error_code='SUPERSEDED_BY_DECISION'`；
  - `input_request.status='cancelled'` 且 `answered_at` 非空。
- 修复前表现：hold 列残留、问题单仍为 `open`。

### 4) `MigratorTests.Migrate_V7_WhenRerunPendingMissing_ThrowsClearErrorAsync`（第 5 项）

- 建极简库：`CREATE TABLE task_view (workspace_id TEXT, id TEXT); PRAGMA user_version = 6;`
- `Assert.ThrowsAsync<InvalidOperationException>(() => Migrator.MigrateAsync(...))`，断言消息包含 `rerun_pending`。
- 修复前表现：抛的是 SQLite "no such column" 类异常（不是目标异常）。

### 5) 文档与既有测试

- 第 3 项无新增测试，改完文档后确认 `DispatchStatusText_MatchesContractVocabulary` 仍通过。

## 验证门禁

```powershell
dotnet build DevHubd.sln
dotnet test DevHubd.sln
node scripts/validate-plugin.mjs
node scripts/check-encoding.mjs
dotnet publish src/DevHubd/DevHubd.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish
# 启动发布版（先确认 3091 空闲），再：
node scripts/smoke-mcp.mjs
```

全部通过后提交并推送（基线 `ff6279e`），然后停下汇报：五项修复、worktree 修复前失败输出、真实时序证据、门禁结果。

## 不做的事

- 不引入新依赖、不改 schema 版本（`CurrentVersion` 保持 7）。
- 不改 `notice` 缺 `workspace_id` 的遗留问题（用户明确本轮不改表）。
- 不把状态/租约复核扩散到失败分支（本轮只要求成功落库路径）。
