# devhub 工具速查

## scheduler_status

入参：`{ "verbose": false }`

返回 `structuredContent` 的关键字段：

| 字段 | 含义 |
|---|---|
| `enabled` / `mode` | 是否启用；`auto` 或 `dispatch_only` |
| `phase` / `pauseReason` | `idle`/`scanning`/`dispatching`/`paused`；暂停原因 |
| `intervalSeconds` | 扫描间隔 |
| `counters` | `scanned`/`enqueued`/`dispatched`/`succeeded`/`failed`/`deferred`/`reclaimed`/`skippedOverlap` |
| `queue` | `pending`/`claimed`/`running`/`dead` 数量 |
| `providers` | 每个 provider 的 `state`、`breaker`、`model`、`checkedAt` |
| `mcp` | `bound`、`port`（固定 3091） |
| `host` | `pid`、`startedAt`、`uptimeSeconds`、`version` |
| `unread` | 未读结果与失败数 |

错误码：

| 码 | 含义 | 可重试 |
|---|---|---|
| `SCHEDULER_UNAVAILABLE` | 数据库或宿主未就绪 | 是 |

## P1 起提供的工具

`queue_list`、`queue_get`、`dispatch_now`、`result_list`、`result_ack`、`scheduler_control`。错误码与语义见设计文档 §11。
