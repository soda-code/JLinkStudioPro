---
name: jlink-studio-pro
description: 通过本机运行的 JLink Studio Pro 上位机（HTTP 网关 127.0.0.1:5080）调试 ARM Cortex-M 目标：连接 J-Link、按符号读写变量、外设寄存器与位域读写、波形时序采样、RTT 日志收发、固件烧写、Keil 编译。当用户提到 J-Link、SWD、STM32/GD32/NXP 在线调试、变量监视、寄存器配置、RTT 日志、烧写固件、Keil 编译时使用本技能。
---

# JLink Studio Pro 调试技能

JLink Studio Pro 是一个 Windows 上位机（WinForms），内嵌 HTTP 网关，把 J-Link 调试能力开放成 REST 接口。
本技能说明如何通过该网关完成常见的嵌入式调试任务。

## 前置条件

1. 上位机正在运行（窗口启动后网关同时监听 `http://127.0.0.1:5080`，仅本机可访问，无鉴权）。
2. 已安装 SEGGER J-Link 软件（V7 及以上，程序会自动探测安装目录，也可用环境变量 `JLINK_STUDIO_DLL_DIR` 指定）。
3. J-Link 调试器已插入，目标板供电正常。

先确认网关在线，再确认连接状态：

```powershell
Invoke-RestMethod http://127.0.0.1:5080/api/v1/session/status
```

`connected` 为 `false` 时先连接（见下）。

## 标准工作流

1. **连接目标**：`POST /api/v1/session/config`
   ```json
   { "device": "STM32F407VG", "interface": "SWD", "speedKhz": 4000, "autoConnect": true,
     "elfPath": "D:\\project\\Objects\\app.axf", "svdPath": "D:\\project\\STM32F407.svd" }
   ```
   `elfPath` 用于符号表（变量读写、波形都依赖它），`svdPath` 用于外设寄存器表。均可留空后在界面里加载。
2. **看现状**：`GET /api/v1/session/status`（连接/型号/符号数/写保护/查看模式）与 `GET /api/v1/data/snapshot`（一次性拿全量状态）。
3. **定位符号**：`GET /api/v1/variables/search?query=g_pid`，或 `GET /api/v1/variables/all`。
4. **读写变量**：`POST /api/v1/variables/read`（body `{"names":["g_pid.kp"]}`）、`POST /api/v1/variables/write`（body `{"name":"g_pid.kp","value":"1.5"}`）。
5. **调寄存器**：`GET /api/v1/registers/peripherals` → `GET /api/v1/registers/register/{peripheral}/{register}` → `POST /api/v1/registers/write-field`。
6. **看时序**：`POST /api/v1/variables/capture-timeseries` 现采一段，或 `GET /api/v1/variables/history?names=a,b` 取回上位机已采的波形历史。
7. **烧固件**：`POST /api/v1/flash/download`（见下「烧写」）。
8. **复盘**：`GET /api/v1/session/logs` 看运行日志，`GET /api/v1/skill/audit` 看操作审计。

## 接口速查（全部以 `http://127.0.0.1:5080` 为前缀）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/v1/skill/manifest` | 能力清单与端点索引（第一步建议先读它） |
| GET | `/api/v1/session/status` | 连接状态、型号、符号数、写保护、最近错误与诊断 |
| GET | `/api/v1/session/logs?take=200&keyword=` | 运行日志（时间正序） |
| POST | `/api/v1/session/config` | 设备/接口/速率/SN/ELF/SVD/RTT 参数；`autoConnect=true` 时立即连接 |
| POST | `/api/v1/session/disconnect` | 断开 |
| POST | `/api/v1/session/control` | `{"action":"reset"｜"halt"｜"go"}` |
| GET/POST | `/api/v1/session/settings` | 查看模式、写保护、采样开关与采样率、RTT 开关、自动重载 ELF |
| POST | `/api/v1/session/reload` | 重新加载 ELF / SVD |
| GET | `/api/v1/session/devices` | 已连接的 J-Link 列表与 SN |
| GET | `/api/v1/data/snapshot` | 全量状态快照 |
| GET | `/api/v1/data/registers` | 寄存器快照 |
| GET | `/api/v1/data/memory?address=0x20000000&length=64` | 读内存，返回 HEX |
| GET | `/api/v1/variables/search?query=` / `/all` | 符号检索 / 全部符号 |
| POST | `/api/v1/variables/read` / `write` / `write-batch` | 变量读写（批量用 `items:[{name,value}]`） |
| POST | `/api/v1/variables/watch` | `{"names":[...],"items":[{name,format,color,plotEnabled,enabled,isWritable,exposeToSkill,refreshRateHz}]}` |
| POST | `/api/v1/variables/unwatch` | 移出监控表 |
| GET | `/api/v1/variables/watchlist` | 监控表当前值与每项属性 |
| POST | `/api/v1/variables/capture-timeseries` | `{"names":[...],"durationMs":2000,"intervalMs":10}` 现采 |
| GET | `/api/v1/variables/history?names=a,b&maxPoints=1000` | 取已采波形历史（抽稀，保留末点） |
| POST | `/api/v1/variables/history/clear` | 清空历史 |
| GET | `/api/v1/registers/peripherals` · `/peripheral/{p}` · `/register/{p}/{r}` | 外设与寄存器（含位域定义、当前值） |
| POST | `/api/v1/registers/write-field` | `{"peripheral":"GPIOA","register":"MODER","field":"MODER5","value":"1"}` |
| POST | `/api/v1/registers/write-register` | 整寄存器写（安全 Read-Modify-Write） |
| GET | `/api/v1/registers/fault-snapshot` | HardFault 现场：核心寄存器 + 故障寄存器 + 最近 RTT + 诊断结论 |
| GET | `/api/v1/rtt/status` · `/logs` | RTT 状态与日志行 |
| POST | `/api/v1/rtt/send` | `{"text":"help\r\n","channel":0,"hex":false,"appendNewLine":false}` |
| POST | `/api/v1/rtt/clear` | 清 RTT 缓冲 |
| GET | `/api/v1/flash/status` | 支持格式、型号、默认地址 |
| POST | `/api/v1/flash/download` | `{"filePath":"D:\\fw\\app.hex","address":"0x08000000","reset":true}` |
| GET | `/api/v1/keil/status` · POST `/api/v1/keil/config`·`build`·`rebuild`·`flash`·`build-flash` | Keil 编译与烧写 |
| GET/POST | `/api/v1/skill/policy` | 写保护策略：`writeEnabled`、`guards`（地址范围白名单）、`exposedSymbols` |
| GET | `/api/v1/skill/audit?take=50` | 操作审计 |
| GET | `/api/v1/ui/state` | 界面当前页面/状态（便于同步给用户看） |
| POST | `/api/v1/ui/prompt` | 在界面上弹提示，让人确认操作 |

Swagger 原始定义：`http://127.0.0.1:5080/swagger/v1/swagger.json`。

## 烧写固件

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:5080/api/v1/flash/download `
  -ContentType 'application/json' `
  -Body '{"filePath":"D:\\fw\\app.hex","reset":true}'
```

- `.bin` **必须**给 `address`（十六进制字符串或十进制）；`.hex/.elf/.axf/.mot/.srec` 用文件内地址，`address` 可省。
- 走 J-Link 内置 Flash 算法，自动擦除所需扇区 + 编程 + 校验；典型耗时数百毫秒到数秒。
- 返回码：`400` 参数/文件问题、`403` 查看模式禁止烧写、`409` 未连接、`200` 成功（含 `elapsedMs`、`running`）。
- 失败信息里带 J-Link 原始错误码与常见原因（型号不匹配、地址越界、读保护、供电/时序）。若提示「未配置目标芯片型号」，先在 `session/config` 里给 `device`。

## 安全约束（务必遵守）

- **查看模式**（`inspectMode=true`）下所有写操作被拒绝，读操作不受影响。接手陌生工程时先看 `session/settings`。
- **写保护**（`writeEnabled` + `guards`）限制变量写入的地址范围；越界会返回拒绝原因。不要为了绕过而关闭保护，先与用户确认。
- 每个写操作都会进 `skill/audit`，动作会在界面运行日志里留痕——用户能看到你做了什么。
- 不确定地址/位域语义时，先读 `registers/register/{p}/{r}` 拿到位域定义与当前值，再决定怎么写。
- 只读诊断（HardFault、卡死）优先用 `registers/fault-snapshot`，它已经把核心寄存器、故障寄存器、最近 RTT 和结论整理好了。

## 常见问题

| 现象 | 处理 |
|---|---|
| 连接被拒 / `session/devices` 为空 | J-Link 未插好或已被 Keil/其它软件占用，先关闭占用方 |
| 符号检索不到变量 | ELF 未加载或编译时被优化掉；检查 `session/status.symbolCount`，必要时 `session/reload` |
| 读到的值一直不变 | 变量被优化进寄存器；加 `volatile` 或用 `data/memory` 按地址读 |
| 烧写返回 400 且提示错误码 | 型号不匹配 / 地址不在 Flash / 读保护；先 `flash/status` 核对型号 |
| 端口 5080 被占用 | 上位机未启动或另一实例在运行，检查进程 |
