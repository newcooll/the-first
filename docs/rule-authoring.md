# CDriveMaster 规则编写指南

本指南面向高阶用户与贡献者，帮助你在不改动核心代码的前提下，为新应用扩展清理能力。

## 核心概念

CDriveMaster 通过扫描 `Rules/*.json` 自动加载规则，并据此构建应用清理能力与界面呈现。

- 新增规则文件后，应用可自动识别并生成对应能力入口。
- 引擎在运行时根据规则内容构建 Bucket、风险分级与执行策略。
- 规则错误不会导致应用整体崩溃，但会在启动时弹出错误告警，提示修复。

## Schema 详解

以下为规则字段中最关键的部分：

### AppName

- 类型：`string`
- 含义：应用显示名称（UI 中展示给用户的名字）。

### Targets

- 类型：`array`
- 含义：待扫描/清理的目标集合。每一项描述一个目录或数据区域。

### BaseFolder

- 类型：`string`
- 含义：目标相对路径。
- 特性：支持 `*/` 通配符，常用于多账号隔离目录。
- 示例：`wxid_*/FileStorage`。

### Kind

- 类型：`string`（枚举）
- 常见值：`Cache`、`Log`、`CrashDump` 等。
- 含义：用于标识目标数据类别，影响展示与审计语义。

### RiskLevel

- 类型：`string`（枚举）
- 可选值：`SafeAuto`、`SafeWithPreview`
- 含义：定义执行风险级别与交互方式：
  - `SafeAuto`：可直接执行的一键清理项。
  - `SafeWithPreview`：需要用户先预览并手动勾选。

## 实战范例

下面是一个假想的 Edge 浏览器规则示例：

```json
{
  "AppName": "Edge",
  "Description": "Microsoft Edge 缓存与日志清理规则",
  "DefaultAction": "DeleteToRecycleBin",
  "Targets": [
    {
      "BaseFolder": "User Data/*/Cache",
      "Kind": "Cache",
      "RiskLevel": "SafeAuto"
    },
    {
      "BaseFolder": "User Data/*/Code Cache",
      "Kind": "Cache",
      "RiskLevel": "SafeAuto"
    },
    {
      "BaseFolder": "User Data/*/Crashpad/reports",
      "Kind": "CrashDump",
      "RiskLevel": "SafeWithPreview"
    },
    {
      "BaseFolder": "User Data/*/Service Worker/Database",
      "Kind": "Log",
      "RiskLevel": "SafeWithPreview"
    }
  ]
}
```

## 加载与调试

1. 将规则文件保存为 `.json`，放入 `Rules` 目录。
2. 重启 CDriveMaster。
3. 在应用中检查新规则是否已出现。

若 JSON 语法错误或字段不合法：

- 应用启动时会弹出红框告警。
- 错误规则会被隔离，不影响其他规则继续加载。

建议调试流程：

1. 先只保留一个最小可用 Target，确认能被识别。
2. 再逐步增加 Target 与通配符路径。
3. 每次修改后重启并观察扫描结果与审计日志，确保行为符合预期。
