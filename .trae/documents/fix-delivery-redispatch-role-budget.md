# b551a08 复核问题修复计划

## Context

独立核验在 `b551a08` 上实测出四类缺陷，其中两类会导致真实业务状态错误：

1. **已交付任务被重跑**：`ResultDecisionStore` 先提交 DB 再写任务文件。文件只读时产生「库 `done/approved`、文件 `ready`」，下一轮 `TaskScanner` 按文件覆盖 DB 为 `ready + approved`，`EnqueueReadyTasksAsync` 再把它当可执行任务入队。
2. **驳回后重派依赖文件字节**：`dedupe_key = workspace:task:contentHash`。文件恰好处于规范字节形态时，驳回后哈希不变，第二次驳回后 `enqueued=0 deduped=1`，任务永久卡在 `ready`。
3. **角色预算字段全部未生效**：`maxRounds/maxToolCalls/maxDurationSeconds/maxParallel/requiresApproval` 只落库展示，回合仍用全局 `ExecutionOptions.ToBudget()`。
4. **`ask_input` 未按能力过滤**：`Describe()` 只按白名单，未按 `canRequestInput` 过滤工具列表。

另有两点只记录、不改行为：`task_upsert` 直写工作区任务目录的边界例外；后台回合可审批其它结果的政策待定。

目标：修复 1–4，补齐失败路径测试；真机复现 1、2 并给出修复前后实测证据；全量门禁通过后提交推送。

## 一、交付态不可回退（问题 1）

### 代码改动

- `src/DevHubd.Core/Results/ResultDecisionStore.cs`
  - 调整顺序：加载结果与 `task_view` 后，**先写任务文件，再提交数据库**。
  - 文件写入抛异常（只读/占用/权限）时：不提交事务，返回新增的 `ResultDecisionStatus.FileWriteFailed` 与原因；结果保持 `awaiting_review`、任务保持原状态。
  - 文件缺失或 JSON 无效：保持现有 Warning 后继续（扫描器会把缺失标 `missing`，不会退回可执行）。
  - 文件写成功但 DB 提交失败：记录 Error；此时文件为 `done`，扫描器不会把它退回 `ready`（与下一条防御配合）。
- `src/DevHubd.Mcp/Abstractions/IResultDecisionSource.cs`
  - 增加 `FileWriteFailed` 枚举值；`src/DevHubd.Mcp/Tools/ResultApproveTool.cs`、`ResultRejectTool.cs` 映射为错误码 `TASK_FILE_WRITE_FAILED`（`DevHubErrorCodes` 新增常量，说明审批未生效、可重试）。
- `src/DevHubd.Core/Tasks/TaskScanner.cs`
  - 新增判定：库内 `approval_state == "approved"`（已交付）时，**不允许**把 `status` 覆盖回 `ready/blocked/missing`；保留 `done`。
  - 依赖阻塞等只在非交付态执行。
  - 首次见到的文件不受影响。
- `src/DevHubd.Core/Queue/QueueService.cs`
  - `EnqueueReadyTasksAsync` 查询增加防御条件：`status == "ready"` 且 `approval_state != "approved"`。
  - 这样即使存在历史遗留的半边状态，也不会入队。

### 测试（`tests/DevHubd.Tests/ResultDecisionTests.cs`、`TaskScannerTests` 相关文件）

- 只读任务文件 + `approve`：断言返回 `FileWriteFailed`，DB 结果仍 `awaiting_review`、`task_view` 仍 `pending/ready`，文件未变；清除只读后再次审批成功，文件与 DB 均为 `done/approved`。
- 历史半边状态：seed `task_view` 为 `done/approved`，磁盘文件 `status=ready`；执行 `ScanAsync` + `EnqueueReadyTasksAsync`，断言 DB 仍 `done`、`enqueued=0`。

## 二、重派去重与文件字节解耦（问题 2）

### 代码改动

- `src/DevHubd.Core/Queue/QueueService.cs`
  - 去重键改为 `workspace:task:g<generation>`，`generation = 该任务已有队列项数 + 1`（同一事务内计算）。
  - 定时入队用一次分组查询批量取各任务计数；手动 `EnqueueTaskAsync` 单任务计数。
  - `content_hash` 列继续写入，仅作追溯，不参与去重。
  - 保留带 `requestId` 的手动幂等键不变。
  - 更新 `BuildDedupeKey` 注释与相关 XML 注释。

### 测试（新增到 `QueueServiceTests.cs` 或 `SchedulerPipelineTests.cs`）

- 规范字节任务：入队 → 队列项置 `succeeded` → 任务回到 `ready`（模拟驳回）→ 再次入队，断言 `enqueued=1/deduped=0`，队列项数为 2。
- 连续两轮：再次置 `succeeded` → `ready` → 入队，断言队列项数为 3，且任务文件字节哈希在第二次驳回前后不变。
- 结合真实 `ResultDecisionStore.DecideAsync(reject)` 的用例：两次「执行→驳回→重派」均产生新队列项。

## 三、角色预算进入回合与并发（问题 3）

### 代码改动

- `src/DevHubd.Core/Models/ModelContracts.cs`
  - `BudgetLimits` 增加非位置属性 `public int? MaxToolCalls { get; init; }`（保持既有构造兼容）。
- `src/DevHubd.Core/Execution/ToolDispatcher.cs`
  - 白名单检查后、执行前检查 `MaxToolCalls`；超限返回 `BUDGET_EXCEEDED`，不递增 `ExecutedCalls`。文件工具与服务工具统一计数。
- `src/DevHubd.Core/Execution/TurnRunner.cs`
  - 先加载角色快照，再计算交付结果与预算。
  - 显式默认常量：`DefaultMaxRounds = 12`、`DefaultMaxToolCalls = 60`、`DefaultMaxDurationSeconds = 1800`、`DefaultMaxParallel = 1`。
  - 回合预算：`MaxToolRounds ← role.MaxRounds`、`MaxToolCalls ← role.MaxToolCalls`、`MaxDuration/Timeout ← role.MaxDurationSeconds`；角色缺失或字段非正时用上述显式默认。
  - `ResolveDeliveryOutcome` 增加角色参数：`role.RequiresApproval == true` 时结果进入 `awaiting_review`（与任务自身 `requiresApproval` 取或）。
  - 其他全局预算（写入数/字节等）保持不变。
- `src/DevHubd.Core/Execution/ExecutionCoordinator.cs` + `src/DevHubd.Core/Queue/QueueService.cs`
  - 构造注入可选 `RoleSnapshotStore`（保持既有测试构造兼容）。
  - `PumpAsync` 每次加载角色 `maxParallel`（夹在 `[1, 全局 MaxParallel]`），维护进程内 `role → 运行中数量`。
  - `ClaimDetailedAsync` 增加可选排除角色集合；达到角色上限的角色本轮不认领，避免消耗 attempt。
  - 认领成功后计数 +1，运行结束 finally 中 -1。

### 测试（`TurnToolSurfaceTests.cs` / `P2ExecutionTests.cs`）

- `Describe()`：`canRequestInput=false` 时列表不含 `ask_input`；`true` 时包含。
- `ToolDispatcher` 预算：`MaxToolCalls=1` 时第二次调用返回 `BUDGET_EXCEEDED`，`ExecutedCalls==1`。
- `TurnRunner` 角色预算：角色声明 `maxRounds=2/maxToolCalls=5/maxDurationSeconds=30`，用 `FakeModelClient` 捕获 `TurnRequest`，断言预算与超时生效；角色 `requiresApproval=true` 时交付为 `awaiting_review`。
- 角色并发：`role_view` 声明 `maxParallel=1`，全局允许 2，同角色两个任务一次 `PumpAsync` 只启动 1 个；完成后下一轮启动第二个。
- `ask_input`：不声明或 `canRequestInput=false` 时既不可见也不可调。

## 四、文档只记录项

- `docs/mcp-tool-contract.md`
  - 新增说明：`task_upsert` 直接写 `<workspace>/.devhub/tasks/`，**有意不经过回合写根**；理由是任务文件是调度控制面产物，必须在 `dispatch_only` 隔离写根下仍可维护，且路径仍经工作区校验与任务校验。
  - 记录待定政策：后台回合可用 `result_approve/result_reject` 审批其它待审结果；是否加闸门等待用户答复，本轮不改行为。
  - 更新审批/驳回段落：文件写入失败时审批不生效并返回 `TASK_FILE_WRITE_FAILED`；去重键改为按任务队列代次，不再引用内容哈希。

## 五、验证与证据

### 全量门禁

```powershell
dotnet build DevHubd.sln
dotnet test DevHubd.sln          # 不加 --nologo
node scripts/validate-plugin.mjs
node scripts/check-encoding.mjs
dotnet publish src/DevHubd -c Release -o artifacts/publish
node scripts/smoke-mcp.mjs       # 需 3091 守护进程
```

### 真机复现（问题 1、2，修复前后各跑一次）

- 用 `artifacts/publish/devhubd.exe` + 临时数据目录 + `workspaces.json` 启动真实守护进程，通过 HTTP MCP 调用 `result_approve/result_reject/queue_list/devhub://tasks`。
- **问题 1**：`result` 置为 `awaiting_review`，任务文件设为只读后审批。
  - 修复前：DB `done/approved`、文件 `ready`；下一轮扫描/入队出现新队列项（复现已交付被重跑）。
  - 修复后：审批返回 `TASK_FILE_WRITE_FAILED`，DB 仍 `awaiting_review`，后续扫描/入队为 0；清除只读后审批成功且文件同步 `done`。
- **问题 2**：规范字节任务文件，连续两轮 `result_reject`。
  - 修复前：第一轮可能重派，第二轮 `deduped=1`。
  - 修复后：两轮均产生新队列项，且第二轮驳回前后文件哈希不变。
- 说明：两项缺陷位于审批/调度路径，真机脚本以真实守护进程 + 真实 MCP 调用验证，模型回合不作为证据来源。

### 收尾

- 提交（含代码、测试、文档），推送 `master`，停并汇报修复点、测试数量、真机证据与提交号。

## 不在本轮范围

- `task_upsert` 改为走回合写根。
- 后台回合审批闸门的行为变更。
- `role.delivery`、`maxInputTokens`、`maxAttempts` 等未列出的字段。
