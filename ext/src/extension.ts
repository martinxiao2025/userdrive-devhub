import * as vscode from 'vscode';

/**
 * 扩展激活入口。P0 只注册一个状态命令，用于验证扩展可装载；
 * vscode.lm 回合桥、runtime.json 探测与通知轮询留到 P3 实现。
 * @param context 扩展上下文
 */
export function activate(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.commands.registerCommand('devhub.showStatus', async () => {
      const response = await fetch('http://127.0.0.1:3091/health').catch(() => undefined);
      if (!response || !response.ok) {
        void vscode.window.showWarningMessage('devhubd 未运行：请先由计划任务或脚本拉起守护进程。');
        return;
      }

      const payload = (await response.json()) as { version?: string; database?: { schemaVersion?: number } };
      void vscode.window.showInformationMessage(
        `devhubd ${payload.version ?? '?'} 在线，schema v${payload.database?.schemaVersion ?? '?'}`
      );
    })
  );
}

/** 扩展停用入口：P3 起在此关闭桥与轮询定时器。 */
export function deactivate(): void {
  // 无需清理（P0 未创建任何长期资源）。
}
