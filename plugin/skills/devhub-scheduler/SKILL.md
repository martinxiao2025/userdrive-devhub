---
name: devhub-scheduler
description: 查询本机 devhub 定时调度器的状态、队列与未读结果。当用户询问"定时任务/调度状态/队列积压"、需要确认某个任务是否已派发，或想手动触发一次扫描时使用。
---

# devhub 定时调度

本技能对接本机常驻进程 `devhubd`（`http://127.0.0.1:3091/mcp`）。它按固定间隔扫描各工作区的 `.devhub/tasks/*.json`，把待办任务放进队列并派发执行。

## 什么时候用

- 用户问"调度器在跑吗""有没有任务卡住""队列里还有多少"。
- 用户想确认某个任务是否已被扫描、是否已派发。
- 用户要手动触发一次扫描或暂停调度。

## 怎么用

1. 先调 `scheduler_status` 看总览：`enabled`、`mode`、`phase`、`pauseReason`、队列计数与 provider 健康。
2. 需要逐条排查时，再查队列与结果的只读资源（P1 起提供）。
3. 写入类操作（暂停/恢复/立即扫描）只在用户明确要求时执行，并复述将要改变的状态。

## 必须注意

- 默认档位是 `dispatch_only`：只派发、结果待人工复核，**不会**自动改工作区、不会提交。
- `providers` 中 `vscode=down` 表示 VS Code 扩展未激活；这**不影响**定时任务走 BYOK 执行。
- 工具返回尾部的 `— devhub · 未读 …` 是未读摘要，不要把它当作用户消息。

更多细节见 `references/tools.md` 与 `references/operations.md`。
