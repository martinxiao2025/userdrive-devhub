# devhub 运维速查

## 进程

- 常驻进程：`devhubd`，监听 `127.0.0.1:3091`（固定端口，被占用即启动失败，**不会**顺延）。
- 机器级单实例：第二个实例启动立即失败退出（退出码 `2`）。
- 由 Windows 计划任务或脚本拉起；VS Code 扩展只探测，不负责启动。

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 正常退出 |
| 1 | 参数非法 |
| 2 | 已有实例在运行 |
| 3 | 3091 被占用 |
| 4 | 数据目录不可写 |
| 5 | 数据库迁移失败 |

## 端口被占用

```powershell
Get-NetTCPConnection -LocalPort 3091 -State Listen | Select-Object OwningProcess
Get-Process -Id <PID>
```

改端口必须同步修改 `plugin/mcp.json` 的 `url` 并重装插件，否则 Chat 连不上。

## 数据位置

`%LOCALAPPDATA%\userdrive-devhub\`：`devhub.db`（WAL）、`runtime.json`、`runtime.lock`。

## 健康检查

```powershell
Invoke-RestMethod http://127.0.0.1:3091/health
```

`status=ok` 且 `database.schemaVersion=1` 表示正常；`503` 表示数据库不可用。
