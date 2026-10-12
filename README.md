# ProcTamer

Windows 上的一个小工具：按进程名压低后台程序的 CPU / 硬盘优先级，并限制它的上传速度。

它是为打游戏时的两类后台干扰写的：反作弊程序在后台扫盘抢资源，对战平台周期性上传数据挤占上行带宽。**开着代理 TUN 模式时也能按进程限上传**（配合 Clash Verge / mihomo），这是系统自带手段做不到的。

- 单个 exe，约 46 KB，不装驱动、不注入、不挂起进程
- 只用 Windows 自带功能，用系统自带的编译器就能从源码构建，不需要装任何开发环境
- 退出时把改过的东西全部还原

![主界面：两条规则正在经中转限速](docs/screenshot.png)

*上图是演示：`curl.exe` 被限在 100 KB/s，另一个进程被限在 50 KB/s，状态列实时显示连接数、上传速率和累计量。*

---

## 目录

- [它解决什么问题](#它解决什么问题)
- [功能一览](#功能一览)
- [快速开始](#快速开始)
- [界面说明](#界面说明)
- [TUN 模式下限上传](#tun-模式下限上传)
- [工作原理](#工作原理)
- [验证是否生效](#验证是否生效)
- [实测数据](#实测数据)
- [已知限制](#已知限制)
- [风险提示](#风险提示)
- [退出、还原与卸载](#退出还原与卸载)
- [从源码构建与修改](#从源码构建与修改)
- [License](#license)

---

## 它解决什么问题

| 现象 | 原因 | ProcTamer 的做法 |
|---|---|---|
| 对局中突然卡一下、掉帧 | 反作弊（如腾讯 ACE 的 `SGuard64.exe`）在后台扫描硬盘，和游戏抢 CPU 与磁盘 | 把它降到最低 CPU 优先级、最低 I/O 优先级，游戏优先用 |
| 对局中周期性跳 ping、丢包 | 对战平台（完美、5E 等）每隔一段时间上传一批数据，上行带宽小的网络会被瞬间占满 | 给这个进程的上传设一个上限，把突发削平 |
| 开着代理 TUN 时限速软件全部失效 | 流量先进虚拟网卡，再由代理进程统一发出，系统已经分不清是谁的流量 | 让代理把指定进程的连接转给 ProcTamer，由它限速后再发出 |

被处理的进程照常运行，只是变慢、不抢资源。ProcTamer 不会结束、挂起或屏蔽任何进程。

> 平台"周期性上传导致丢包"的说法来自玩家用流量监控工具的个别观察，并未大范围验证。是否对你有效，请按[验证是否生效](#验证是否生效)自己确认。

## 功能一览

| 功能 | 做什么 | 需要管理员 | TUN 下可用 |
|---|---|---|---|
| 效能模式 | 发现进程在运行时，设为低优先级 + 效能模式（EcoQoS）+ 极低 I/O 优先级 | 普通进程不需要 | 是 |
| 注册表优先级 | 让系统在进程**启动时**直接给低优先级，用于会拒绝访问的受保护进程 | 需要 | 是 |
| 限上传（系统 QoS） | 用 Windows 策略式 QoS 按程序名限速 | 需要 | **否** |
| 限上传（本机中转） | 代理把该进程的 TCP 转给 ProcTamer，用令牌桶限上传，下载不限 | 不需要 | 是 |
| 同步到 Clash | 把要限速的进程名单自动写进 Clash Verge 的全局扩展脚本，增减进程后自动更新 | 不需要 | 是 |
| 状态显示 | 每条规则是否生效、进程是否在运行、中转连接数 / 速率 / 累计量 | — | — |
| 托盘常驻、开机自启 | 关窗缩到托盘；可登记为登录时以管理员权限静默启动 | 自启需要 | — |

"限上传 KB/s"是同一个数值，两种限速方式都用它：没开 TUN 时由系统 QoS 执行，开了 TUN 且配好代理规则后由本机中转执行。

## 快速开始

**环境**：Windows 10 / 11（64 位）。系统自带的 .NET Framework 4.x 即可，不需要另外安装任何东西。

1. 下载本仓库（`Code → Download ZIP` 并解压，或 `git clone`）。
2. 双击 `build.cmd`，同目录下生成 `ProcTamer.exe`。
3. 运行 `ProcTamer.exe`。首次运行会载入一组预设规则，**全部默认不启用**。
4. 在要处理的进程那一行勾上"启用"，按需勾选功能或填写限速值。
5. 需要"注册表优先级"、系统 QoS 限速或开机自启时，点工具栏的"以管理员身份重启"。
6. 游戏时开着代理 TUN 的话，再按 [TUN 模式下限上传](#tun-模式下限上传) 配一次代理。

预设规则：

| 进程 | 所属 | 效能模式 | 注册表优先级 | 限上传 KB/s |
|---|---|---|---|---|
| `SGuard64.exe` | 腾讯 ACE 反作弊 | 是 | 是 | 0 |
| `SGuardSvc64.exe` | 腾讯 ACE 反作弊 | 是 | 是 | 0 |
| `完美世界竞技平台.exe` | 完美平台主程序 | 否 | 否 | 100 |
| `pwautil64.exe` | 完美平台组件 | 否 | 否 | 100 |
| `WmgpOptimizer_x64.exe` | 完美平台组件 | 否 | 否 | 100 |
| `5EClient.exe` | 5E 平台主程序 | 否 | 否 | 100 |
| `Bucky64.exe` | 5E 目录下的组件（推测为反作弊） | 否 | 否 | 100 |

这只是起点，任何进程都可以用"添加进程"加进来。

## 界面说明

### 列

| 列 | 含义 |
|---|---|
| 启用 | 这条规则的总开关。不勾则后面的设置都不生效 |
| 进程 | 程序名（不含路径），不区分大小写 |
| 效能模式 | 进程运行时把它设为低优先级 + 效能模式 + 极低 I/O 优先级 |
| 注册表优先级 | 在注册表登记"此程序启动时即为低优先级"。只对之后启动的进程生效 |
| 限上传 KB/s | 上传速度上限，`0` = 不限。1 KB 按 1000 字节计 |
| 状态 | 这条规则当前的实际效果，见下表 |

### 状态列常见内容

| 显示 | 含义 |
|---|---|
| `未启用` / `已暂停` | 规则没在生效 |
| `未运行` / `运行中×3` | 该进程当前的实例数 |
| `效能✔` | 效能模式已套用 |
| `效能✘ 拒绝访问` | 进程受保护，改不动。改用"注册表优先级" |
| `注册表✔` | 注册表项已写入，下次启动该进程时生效 |
| `注册表✘ 需管理员` | 当前不是管理员 |
| `注册表✘ 已有其他设置` | 该程序已有别人写的优先级设置，ProcTamer 不覆盖 |
| `限速✔ 100KB/s` | 系统 QoS 策略已建立 |
| `限速✘ 流量走 Meta（TUN），等待中转连接` | 检测到 TUN，系统 QoS 无效；还没有连接经中转过来（代理没配好，或该进程没在联网） |
| `限速✘ 中转未启动` | 本机 17877 端口监听失败，底部状态栏会给出原因 |
| `中转 2 连接  ↑ 37.5 KB/s（限 100）  累计 1.2 MB` | 正在经中转转发：当前连接数、上传速率、累计上传量 |

### 工具栏与托盘

- **添加进程 / 删除**：增减规则。添加时可从正在运行的进程里选，也可直接输入程序名。
- **暂停全部**：让所有规则临时失效。中转照常转发，只是不限速。
- **开机自启**：登记一个登录时触发的计划任务，以管理员权限静默启动到托盘。
- **同步到 Clash**：把限上传大于 0 的进程写进 Clash Verge 的全局扩展脚本，按钮高亮表示已开启。再点一次移除。详见 [TUN 模式下限上传](#tun-模式下限上传)。
- **以管理员身份重启**：仅在非管理员运行时显示。
- **托盘图标**：绿色 = 运行中，灰色 = 已暂停，橙色 = 非管理员。双击显示主窗口。
- 点窗口的关闭按钮只是缩到托盘；托盘右键 **"退出并还原"** 才是真正退出。

## TUN 模式下限上传

### 为什么需要额外配置

Clash Verge、mihomo 等代理开启 TUN 后，所有程序的流量先进入一块虚拟网卡，再由代理进程从物理网卡发出。此时：

- Windows 的 QoS 限速只绑定在物理网卡上，虚拟网卡上不生效；
- 到了物理网卡，流量已经全部属于代理进程，无法再按原程序区分。

所以系统自带的按进程限速（QoS 策略、作业对象）在 TUN 下都无效。ProcTamer 的做法是换一个位置限速：**代理仍然知道每条连接来自哪个进程，让它把指定进程的连接转给 ProcTamer。**

```
被限速的进程
   │  TCP
   ▼
虚拟网卡（TUN）
   │
   ▼
代理内核（mihomo）── 按进程名匹配规则
   │  SOCKS5，用户名 = 进程名
   ▼
ProcTamer 本机中转 127.0.0.1:17877 ── 令牌桶限上传
   │  由 ProcTamer.exe 发出
   ▼
代理内核 ── 规则：ProcTamer.exe 一律直连
   │
   ▼
物理网卡 → 互联网
```

### Clash Verge：点一下"同步到 Clash"（推荐）

1. 在 ProcTamer 工具栏点 **"同步到 Clash"**，确认。
2. 到 Clash Verge 的订阅（Profiles）页，在当前订阅卡片上点一次"使用"，让它重新生成配置。

之后在 ProcTamer 里添加 / 删除进程，或把某个进程的限速值在 0 和非 0 之间改动，脚本会自动更新，状态栏会提示"Clash 规则已更新"，这时再去点一次"使用"即可。只是勾选 / 取消"启用"、点"暂停全部"或调整限速数值，不需要动 Clash，立即生效。

这个功能改的是 Clash Verge 的全局扩展脚本 `%APPDATA%\io.github.clash-verge-rev.clash-verge-rev\profiles\Script.js`，规则如下：

- **只改两个标记之间的那一段**，脚本里别的内容原样保留：

  ```js
  // BEGIN PROCTAMER
  // ……由 ProcTamer 自动维护的 procTamerRelay(config) 函数……
  // END PROCTAMER

  function main(config, profileName) {
    config = procTamerRelay(config);   // ProcTamer 加的唯一一行调用
    // 你自己的其他逻辑可以继续写在这里
    return config;
  }
  ```

- 第一次写入前会把原文件备份到上一级目录，文件名形如 `Script.js.bak-<时间>-proctamer`。
- 脚本原本是空白模板（或旧版的独立 `proctamer-relay.js`）时，直接生成上面的结构；已经有你自己的内容时，把那一段加在最前面，并在你的 `main` 函数第一行补上那句调用。
- 写进名单的是**所有限上传大于 0 的规则**，不管是否勾了"启用"。启用与否只决定中转里限不限速。
- 再点一次"同步到 Clash"会移除那一段和那句调用，其余内容不动。
- 找不到 Clash Verge 时按钮会提示，不做任何修改。

### Clash Verge：手动使用现成脚本

不想让 ProcTamer 改 Clash 的文件，可以手动安装。仓库里的 [`proctamer-relay.js`](proctamer-relay.js) 是给 Clash Verge 的**全局扩展脚本**用的独立版本，效果与自动生成的那一段完全相同。

1. 打开 Clash Verge → 订阅（Profiles）页 → "全局扩展脚本"（Global Extend Script）。不同版本的菜单文字可能略有出入。
2. 先把里面原有的内容复制出来留个备份，再用 `proctamer-relay.js` 的内容整体替换，保存。
3. Clash Verge 会重新生成配置。没生效的话，在当前订阅卡片上再点一次"使用"。

### 脚本做了什么

脚本（两种方式相同）在订阅原有内容的基础上追加节点、策略组和规则，不改动原有配置。它为列表里的每个进程做三件事：

| 追加的内容 | 名称 | 说明 |
|---|---|---|
| 节点 | `ProcTamer-Relay <进程名>` | `socks5`，`127.0.0.1:17877`，用户名 = 进程名 |
| 策略组 | `ProcTamer <进程名>` | `fallback`：`[上面的节点, DIRECT]`，每 30 秒健康检查一次 |
| 规则 | `AND,((PROCESS-NAME,<进程名>),(NETWORK,TCP)),ProcTamer <进程名>` | 只匹配该进程的 TCP |

另外在最前面加一条 `PROCESS-NAME,ProcTamer.exe,DIRECT`，保证 ProcTamer 自己发出的连接不会被再次转回中转。

因为策略组是 `fallback`，**ProcTamer 没运行时健康检查会失败，流量自动走直连**，不会断网。

手动安装时，要增减进程得自己改脚本开头的 `PROCESSES` 数组，并在 ProcTamer 里添加同名规则（用"同步到 Clash"则不需要）：

```js
const PROCESSES = [
  "完美世界竞技平台.exe",
  "pwautil64.exe",
  "WmgpOptimizer_x64.exe",
  "5EClient.exe",
  "Bucky64.exe"
];
```

### 其他 mihomo 客户端：手写配置

不用 Clash Verge 的话，把下面的内容按进程各写一份加进配置即可，规则必须放在其他规则之前：

```yaml
proxies:
  - name: ProcTamer-Relay 5EClient.exe
    type: socks5
    server: 127.0.0.1
    port: 17877
    username: 5EClient.exe      # 用户名就是进程名
    password: proctamer         # 任意非空值，中转不检查密码
    udp: false

proxy-groups:
  - name: ProcTamer 5EClient.exe
    type: fallback
    proxies:
      - ProcTamer-Relay 5EClient.exe
      - DIRECT
    url: http://www.msftconnecttest.com/connecttest.txt
    interval: 30
    timeout: 3000
    lazy: false

rules:
  - PROCESS-NAME,ProcTamer.exe,DIRECT
  - AND,((PROCESS-NAME,5EClient.exe),(NETWORK,TCP)),ProcTamer 5EClient.exe
```

任何能"按进程名把 TCP 连接转给一个带用户名的 SOCKS5 出口"的代理软件都可以用同样的思路接入。

### 撤销

用"同步到 Clash"装的：再点一次这个按钮，然后到 Clash Verge 点一次"使用"。

手动装的：把全局扩展脚本恢复成默认内容并保存：

```js
// Define main function (script entry)

function main(config, profileName) {
  return config;
}
```

只是临时不想限速的话不用动代理：在 ProcTamer 里点"暂停全部"，或直接退出 ProcTamer（约 30 秒内自动回落直连）。

## 工作原理

### 效能模式

定时器每 2 秒按进程名查找，对匹配的进程调用：

- `SetPriorityClass(IDLE_PRIORITY_CLASS)`：最低 CPU 优先级；
- `SetProcessInformation(ProcessPowerThrottling)`：开启 EcoQoS，也就是任务管理器里的"效能模式"；
- `NtSetInformationProcess(ProcessIoPriority = VeryLow)`：最低磁盘 I/O 优先级。

处理过的进程会被记下来，规则停用、暂停或程序退出时恢复为普通优先级。恢复前会核对进程名，避免进程号被复用后误改别的进程。

### 注册表优先级

写入

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<程序名>\PerfOptions
    CpuPriorityClass = 1   (Idle)
    IoPriority       = 0   (VeryLow)
    ProcTamer        = 1   (本程序的标记)
```

这是 Windows 自带的机制：内核在创建进程时直接套用这些值，所以对拒绝外部修改的受保护进程也有效。代价是只对之后启动的进程生效。

ProcTamer 只动带有自己标记的项：已有别人写的 `PerfOptions` 时不覆盖，删除时也只删带标记的。

### 限上传：系统 QoS

通过 Windows PowerShell 调用 `New-NetQosPolicy`，按程序名建立限速策略，写入 `ActiveStore`（不落盘，重启后自动消失）。策略名以 `ProcTamer_` 开头，规则变化时先删后建，退出时全部删除。

程序会检测外网流量的出口网卡：如果是虚拟网卡，就在状态里如实标明这种限速不生效。

### 限上传：本机中转

ProcTamer 启动时在 `127.0.0.1:17877` 开一个 SOCKS5 服务：

- 只监听本机回环地址，独占端口，局域网访问不到；
- 只接受用户名 / 密码认证，**用户名就是进程名**，密码不检查；
- 只支持 `CONNECT`（TCP）。`UDP ASSOCIATE`、`BIND`、不带用户名的连接一律拒绝；
- 目标地址支持 IPv4、IPv6 和域名（域名由 ProcTamer 自行解析，优先 IPv4）。

限速用的是**令牌桶**，同一进程的所有连接共用一个桶：

| 参数 | 取值 |
|---|---|
| 速率 | `限上传 KB/s × 1000` 字节 / 秒 |
| 桶容量 | 0.25 秒的量，最少 4096 字节 |
| 最小放行块 | `速率 / 50`，最少 512 字节（避免把数据切成很多小包） |

桶里的令牌不够时，ProcTamer 停止从代理读取数据。TCP 的流量控制会把这个压力一路传回原进程，让它自己慢下来，数据不会在中间堆积。

下载方向（外网 → 进程）不经过令牌桶，全速转发。规则未启用、已暂停、限速为 0，或用户名没有对应规则时，照常转发、不限速。

## 验证是否生效

**1. 中转在监听**

```powershell
Get-NetTCPConnection -LocalPort 17877 -State Listen
```

**2. 手动走一次中转**（用户名随便填一个规则里的进程名）

```powershell
curl.exe --socks5-hostname 127.0.0.1:17877 --proxy-user "5EClient.exe:x" http://www.msftconnecttest.com/connecttest.txt
```

应输出 `Microsoft Connect Test`，同时 ProcTamer 里 `5EClient.exe` 那一行会出现中转统计。

**3. 代理规则已加载**：Clash Verge 的规则页最前面能看到 `PROCESS-NAME,ProcTamer.exe,DIRECT` 和各进程的 `AND,(...)` 规则；代理页里 `ProcTamer <进程名>` 组在 ProcTamer 运行时选中的是 `ProcTamer-Relay ...`。

**4. 真实流量**：目标进程运行时，代理的连接页里它的 TCP 连接应显示走 `ProcTamer-Relay`；ProcTamer 状态列显示连接数，上传速率不超过限速值。

**5. 判断限速对你有没有用**：如果游戏里周期性的上行突发仍然存在，而对应进程的中转累计量几乎不增长，说明那部分数据走的是 UDP，本工具管不到（见[已知限制](#已知限制)）。

## 实测数据

测试环境：Windows 11，Clash Verge 2.5.8（Mihomo Meta v1.19.32），TUN 开启。上传目标是本机的 HTTP 接收端，用改名后的 `curl.exe` 模拟目标进程。

| 测试 | 结果 |
|---|---|
| 规则 100 KB/s，上传 500 KB | 4.78 秒（理论值 4.75 秒） |
| 规则 50 KB/s，中文进程名，经 mihomo 进程规则转入，上传 500 KB | 9.76 秒（理论值 9.75 秒） |
| 同一规则下载 5 MB | 0.02 秒，不受限 |
| 规则未启用，或用户名没有对应规则 | 照常转发，不限速 |
| 不带用户名连接中转 | 被拒绝 |
| 不在代理规则里的进程 | 按原有规则走，不经过中转 |
| ProcTamer 退出后 | 健康检查失败，同一进程自动改走直连 |
| TUN 开启时代理连接 `127.0.0.1:17877` | 健康检查通过，延迟 50–74 ms |

理论值的算法：`(数据量 − 桶容量) ÷ 速率`。例如 `(500000 − 25000) ÷ 100000 = 4.75` 秒。

尚未充分实测的部分，请知悉：

- "注册表优先级"和系统 QoS 限速需要管理员权限，目前只做过功能实现，没有在真实反作弊进程上做长时间验证；
- 对战平台真实对局中的限速效果，取决于它的上传走 TCP 还是 UDP。

## 已知限制

- **只管 TCP。** UDP 流量不经过中转，仍按代理原有规则走，不限速。
- **经中转的进程一律直连出网。** 这些进程的 TCP 由 ProcTamer 直接发出，不再按代理订阅里的域名规则分流。原本会走代理节点的连接也会变成直连。
- **退出时有短暂空窗。** ProcTamer 退出后到代理下一次健康检查之间（最长约 30 秒），这些进程的新连接会失败，之后自动回落直连；退出时已建立的中转连接会断开。
- **健康检查会留下痕迹。** 代理每 30 秒检查一次中转是否可用，状态列里会偶尔出现 1 个连接和少量累计流量，这是正常的。
- **改了名单要让 Clash 重新加载。** 用"同步到 Clash"时脚本会自动更新，但 Clash Verge 只在重新生成配置时才读脚本，仍需手动点一次"使用"。手动安装脚本的话，进程名单要两边都改。
- **"同步到 Clash"只认 Clash Verge。** 它按固定路径找 Clash Verge 的全局扩展脚本，其他客户端请手写配置。进程名里带逗号或括号的不会写进名单（规则语法不允许）。
- **注册表优先级不即时生效。** 只对之后启动的进程有效，要在启动游戏前设置好。
- **异常退出不会还原。** 程序被强制结束或系统崩溃时来不及清理：QoS 策略重启后自动消失；注册表项会保留，下次启动 ProcTamer 时按配置重新同步。
- **系统 QoS 在部分家用环境下可能不生效。** 这是 Windows 策略式 QoS 本身的特性，状态列显示 `限速✔` 只代表策略已建立，是否真的压住了上行请用资源监视器确认。

## 风险提示

- 本工具会降低反作弊、对战平台等程序的优先级并限制其网络。这些做法**没有任何官方许可依据**，平台方是否把它视为异常行为无法保证。
- 限速值设得过低，可能导致平台数据传不完、连接超时，后果未知（例如被判定掉线）。建议从 100 KB/s 这样较宽松的值开始。
- ProcTamer 不修改游戏、不读写其他进程的内存、不拦截或伪造任何数据，只调整优先级和转发速度。但使用后果由使用者自行承担。

## 退出、还原与卸载

托盘右键"退出并还原"时会依次：

1. 关闭本机中转；
2. 把处理过的进程恢复为普通优先级；
3. 删除本程序写入的注册表项（只删带标记的）；
4. 删除所有 `ProcTamer_*` QoS 策略。

卸载：

1. 如果开过"开机自启"，先在程序里点一次把它关掉（或在任务计划程序里删除名为 `ProcTamer` 的任务）；
2. 如果用过"同步到 Clash"，再点一次把那一段移除（手动装的脚本则自己恢复成默认内容），并到 Clash Verge 点一次"使用"；
3. 退出并还原；
4. 删除程序所在目录。

手动检查是否有残留（管理员 PowerShell）：

```powershell
Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object Name -like 'ProcTamer_*'
Get-ScheduledTask -TaskName ProcTamer -ErrorAction SilentlyContinue
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options' |
    Where-Object { (Get-ItemProperty "$($_.PSPath)\PerfOptions" -ErrorAction SilentlyContinue).ProcTamer }
```

三条命令都没有输出就是干净的。

## 从源码构建与修改

### 文件

| 文件 | 说明 |
|---|---|
| `ProcTamer.cs` | 全部源码，单文件 |
| `app.manifest` | 清单：普通权限启动、DPI 感知、新版控件样式 |
| `build.cmd` | 一键构建脚本 |
| `proctamer-relay.js` | Clash Verge 全局扩展脚本的独立版本，供手动安装 |
| `rules.tsv` | 运行后自动生成的配置文件（不在仓库里） |

### 构建

`build.cmd` 实际执行的是：

```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 ^
  /codepage:65001 /win32manifest:app.manifest /out:ProcTamer.exe ^
  /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll ProcTamer.cs
```

修改源码时注意：

- 系统自带的这个编译器只支持到 **C# 5**。不能用字符串内插（`$"..."`）、`?.`、`out var`、表达式体成员、`nameof` 等更新的语法；`async` / `await` 可以用，但 `catch` / `finally` 里不能 `await`。
- 源码是 UTF-8 编码，所以构建参数里有 `/codepage:65001`。
- `build.cmd` 必须保持 **CRLF** 换行，否则 cmd 会把中文注释的一部分当成命令执行。仓库的 `.gitattributes` 已做了约束。
- 程序运行时 exe 被占用，重新构建前要先退出。

### 配置文件格式

`rules.tsv` 与 exe 放在同一目录，UTF-8 编码，每行一条规则，用制表符分隔：

```
启用	进程名	效能模式	注册表优先级	限速KB/s
```

布尔值用 `1` / `0`。例如：

```
1	5EClient.exe	0	0	100
```

删除这个文件再启动，就会恢复成预设规则。

### 命令行参数

| 参数 | 作用 |
|---|---|
| `--tray` | 启动后不显示窗口，直接进托盘（开机自启用的就是它） |
| `--wait` | 等待上一个实例退出后再启动（提权重启时内部使用） |
| `--shot <png路径>` | 调试用：启动后把窗口画成图片保存并退出，不占用单实例锁 |
| `--shot-after <毫秒>` | 配合 `--shot`，指定多久之后截图，默认 1500。不要和 `--tray` 同用 |
| `--clash-dir <目录>` | 指定 Clash Verge 的数据目录（里面要有 `profiles\Script.js`），默认是 `%APPDATA%\io.github.clash-verge-rev.clash-verge-rev`。`--shot` 模式下不指定就不碰 Clash 的文件 |

## License

[MIT](LICENSE)
