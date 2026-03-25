# CDriveMaster Manual Acceptance

## Beta Release Gates

### 环境兼容性矩阵

| 操作系统 | 权限状态 | 运行环境 | 测试人 | 结果 | 备注 |
| --- | --- | --- | --- | --- | --- |
| Windows 10 22H2 | UAC 提权 | 已安装 .NET 8 运行时 |  |  |  |
| Windows 10 22H2 | UAC 提权 | 未安装 .NET 8 运行时 |  |  |  |
| Windows 10 22H2 | 普通用户 | 已安装 .NET 8 运行时 |  |  |  |
| Windows 10 22H2 | 普通用户 | 未安装 .NET 8 运行时 |  |  |  |
| Windows 11 23H2 | UAC 提权 | 已安装 .NET 8 运行时 |  |  |  |
| Windows 11 23H2 | UAC 提权 | 未安装 .NET 8 运行时 |  |  |  |
| Windows 11 23H2 | 普通用户 | 已安装 .NET 8 运行时 |  |  |  |
| Windows 11 23H2 | 普通用户 | 未安装 .NET 8 运行时 |  |  |  |

### 破坏性测试与失败预期 (Chaos Engineering)

| 用例 | 注入方式 | 操作步骤 | 预期行为 | 实际结果 | 结论 |
| --- | --- | --- | --- | --- | --- |
| A: 规则文件破坏 | 手动破坏 Rules/wechat.json JSON 格式 | 启动应用并执行应用扫描 | UI 不崩溃，顶部弹窗告警，QQ/Chrome 仍可扫描与执行 |  |  |
| B: 写权限撤销 | 移除目标缓存目录写权限 | 执行 SafeAuto 真实清理 | 底层记录执行失败，UI 显示红色失败状态，审计日志写入错误说明 |  |  |
| C: 磁盘濒危 | 用大文件填满 C 盘剩余空间 | 执行 StartComponentCleanup | 进程可观测，不应无响应卡死；失败时可回收提示和日志记录完整 |  |  |

## 1. Rule Load Failure Isolation
- Start the app with at least one malformed rule file under `Rules/`.
- Verify the app still opens and other valid rule providers are available.
- Trigger a scan from a valid provider and confirm scan succeeds.
- Expected: invalid rule is skipped, app does not crash, and valid providers work normally.

## 2. SafeAuto Real Apply + Audit
- Open a provider that supports `SafeAuto` and run scan.
- Execute real cleanup flow and confirm operation in dialog.
- After completion, open logs folder and verify an audit JSON file is generated in `Logs/`.
- Expected: execution summary is shown, and audit file contains non-skipped executed buckets.

## 3. SafeWithPreview Selective Apply (High Volume)
- Use a provider that supports preview mode and produces many entries.
- In preview dialog, select part of the items and run apply.
- Validate only selected entries are executed.
- Expected: UI remains responsive during preview, and result summary reflects selected subset.

## 4. System Maintenance Guarded Cleanup
- Open System Maintenance page and run analysis.
- Verify cleanup button is disabled before confirmation.
- Check consent box, run cleanup, and wait for completion.
- Expected: cleanup requires analysis + consent; after run, guard is locked again and result section is visible.

## 5. System Maintenance Re-Analyze + Logging
- On System Maintenance page, click audit log button and confirm `Logs/` opens.
- After one cleanup run, verify a `Audit_SystemMaintenance_*.json` file exists.
- Click re-analyze and verify previous result is cleared before new analysis values show.
- Expected: no stale metrics, cleanup state resets, and logs are accessible.
