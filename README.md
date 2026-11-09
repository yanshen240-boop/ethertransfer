# EtherTransfer 以太传输

**局域网点对点文件传输 —— 一根网线直连，或同一个 Wi-Fi，不经过任何云服务器。**

电脑端（Windows，.NET 10 + Avalonia）与安卓端（原生 Java）双端互通，支持文件、文件夹、文字消息互传。

| 端 | 版本 | 运行环境 |
| :--- | :--- | :--- |
| Windows 桌面端 | 0.7.0 | Windows 10 / 11 x64，免安装单文件，内含 .NET 运行时 |
| Android 客户端 | 1.0.0（versionCode 16） | Android 8.0+（minSdk 26 / targetSdk 35） |

---

## 一、直接下载运行

**推荐从 [Releases](https://github.com/yanshen240-boop/ethertransfer/releases/latest) 直接下载**，不用克隆 60MB 的仓库：

| 文件 | 直链 |
| :--- | :--- |
| Windows 单文件版（62.6 MB） | [EtherTransfer-0.7.0-win-x64.exe](https://github.com/yanshen240-boop/ethertransfer/releases/download/v0.7.0/EtherTransfer-0.7.0-win-x64.exe) |
| Android 安装包（145 KB） | [EtherTransfer-1.0.0-android.apk](https://github.com/yanshen240-boop/ethertransfer/releases/download/v0.7.0/EtherTransfer-1.0.0-android.apk) |

安装包同时也存放在仓库的 `releases/` 目录里，与源码一起管理。

**Windows 首次运行**会弹出防火墙提示，请勾选"专用网络"并允许，否则对端发现不到本机。

**安卓安装后**建议在系统设置里允许"后台常驻 / 关闭电池优化"，并在应用内授予通知权限，这样锁屏时也能收到传输请求。

---

## 二、功能详解

### 1. 设备发现：不用输 IP，打开就能看见对方

两端启动后会周期性向局域网广播 UDP（端口 `50000`）宣告自己，广播包里带设备名、TCP 监听端口和网卡地址。收到后校验来源网段，过滤掉路由器、电视、虚拟网卡与 VPN 网卡，只显示真正运行了 EtherTransfer 的设备，并在设备名后面标注这条链路是「有线」还是「Wi-Fi」。

- 有线直连（网线互插）时，两端会自动协商 `169.254.x.x` 链路本地地址（RFC 3927），不需要路由器、不需要 DHCP、不需要手动配 IP。
- 同一 Wi-Fi 下手机与电脑也能互相发现；电脑可以同时插着网线直连另一台电脑，两条链路并存。
- 安卓端额外支持**手动添加设备**（设置页），用于路由器开了 AP 隔离、广播被拦掉的环境。

### 2. 状态灯：显示"现在能不能传"，而不是"刚才有没有出现过"

这是本项目最容易被忽略但最实用的设计。设备名右侧的小圆点：

| 颜色 | 含义 |
| :--- | :--- |
| 灰 | 还没检测过 |
| 绿 | 刚做了一次真实建连测试，确认可传（鼠标悬停可看走的哪条链路、往返多少毫秒） |
| 红 | 所有链路都试过、连唤醒包加深探测也没回执，现在确实传不过去 |

检测方式是**便宜的单轮握手**：不是 ping，而是真的按协议发起一次连接并等回执；收到回执就算通（对端回"正在处理另一项传输"说明它是忙，不是断）。右上角刷新按钮会立刻补发一轮广播并逐台复测；不点它也会自动巡检——全部正常时 30 秒一轮，有设备不通时 8 秒一轮，检测到正在传输则自动跳过本轮，绝不和对端抢那个唯一的接受窗口。

### 3. 传输引擎：吃满千兆网线

- 32 MB 双缓冲通道（`System.Threading.Channels`）做流水线，直通式内核 I/O，无中间拷贝，实测约 **115 MB/s**，基本跑满千兆链路。
- 文件夹传输用零分配二进制分帧，接收端 4–16 个工作线程并发落盘，专治 NTFS 小文件延迟。
- 跨设备保留原文件与目录的**创建时间、修改时间**（`CreationTimeUtc` / `LastWriteTimeUtc`）。
- 接收端先写临时文件、校验路径后再落盘；中断自动清理，绝不覆盖已有同名文件——每次接收都进独立目录。

### 4. 双链路：有线优先，拔线不断连

一台电脑同时接有线和 Wi-Fi 时，两端地址按会话合并显示为同一台设备，优先走有线。链路选择不是"猜"，而是**按端点绑定本机源地址再建连**（Windows 强主机模型下，源地址绑错会静默丢包），这是双链路场景唯一可靠的做法。拔掉网线不会直接掐断正在进行的无线 TCP 传输，而是等本轮失败后按有线 1.5s / 4s、无线 4s / 10s / 10s 的重试节奏（无线含唤醒包）重新建连。

### 5. 文字消息：像聊天一样传话（带送达回执）

除了文件，两端还能互发文字。桌面端有会话记录列表，支持按关键字**搜索**会话记录、一键**清理**；消息带 `TEXT_ACK` 回执，发送方能看到是否送达，未送达的消息会在对端重新上线后补发。适合没有加好友、只想在局域网内传一句话或一个验证码的场景。

### 6. 安卓端的三个关键实现细节

- **接收默认写 `Download/EtherTransfer`**（走 MediaStore，免存储授权）。Android 11+ 不允许应用授权"下载"根目录，所以默认走 MediaStore，SAF 目录授权只作为可选项；每次接收创建 `EtherTransfer-时间戳` 子目录。
- **前台服务 + WifiLock + WakeLock**：锁屏后仍能收文件，避免"手机放兜里就收不到"。
- **网络栈单例**（`singleTop` + `netStarted`）：防止重复启动产生孤儿 `ServerSocket`，导致对端 SYN 被系统静默丢弃——这类问题表现为"能发现但一连就超时"，很难查。

### 7. 兼容既有协议，不重造轮子

安卓端实现的是电脑端已有的 EtherTransfer V1 协议，所以两端天然互通：

| 环节 | 端口 / 帧 |
| :--- | :--- |
| 设备发现 | UDP `50000`，广播 + 定向单播 |
| 文件传输 | TCP，端口由发现包动态通告，长度前缀 JSON 帧 |
| 帧类型 | `TRANSFER_REQUEST` / `TRANSFER_RESPONSE` / `FILE_BEGIN` / `FOLDER_BEGIN` / `FILE_SKIP` / `TRANSFER_END` / `TEXT_MESSAGE` / `TEXT_ACK` |
| 文件数据 | 原始字节流，分块传输 |
| AP 隔离兜底 | UDP `39000`（数据）/ `39001`（探测），900 字节分片、32 窗口 ARQ，用于路由器禁止终端互访时的 Wi-Fi 场景 |

---

## 三、仓库结构

```
EtherTransfer.slnx              解决方案
EtherTransfer.UI/               桌面端界面（Avalonia 12，AXAML + 主窗口/对话框）
EtherTransfer.Core/             领域模型：设备、会话、协议帧常量、传输结果
EtherTransfer.Network/          网卡探测、链路本地地址、UDP 发现、链路路由（LinkRouter）
EtherTransfer.Services/         设备管理、会话管理
EtherTransfer.Transfer/         传输引擎：流水线流、发送器、接收器、路径净化
EtherTransfer.Android/          安卓端（原生 Java，无 Gradle、无第三方运行时）
  ├── src/cn/ethertransfer/mobile/   界面页 + UDP/TCP 协议实现
  ├── res/                           资源与图标
  └── build.ps1                      javac + d8 + aapt2 + apksigner 一键构建
EtherTransfer.Tests/            单元测试（双链路、协议、路径净化等）
docs/                           架构与协议文档
releases/                       已构建好的安装包
使用说明.md                      面向用户的上手说明
```

代码规模：C# 约 14,300 行，Java 约 6,600 行。

---

## 四、自己编译

电脑端（需 .NET 10 SDK）：

```bash
dotnet build EtherTransfer.UI -c Release
dotnet test  EtherTransfer.Tests -c Release      # 104 通过 / 5 跳过为基线

# 生成免安装单文件版
dotnet publish EtherTransfer.UI -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PortableBuild=true -o publish/win-x64
```

> 打包前请先关闭正在运行的 `EtherTransfer.exe`，否则单文件打包会因文件占用失败（MSB4018）。

安卓端（需 JDK 21 + Android SDK，**不需要 Gradle**）：

```powershell
powershell -File EtherTransfer.Android/build.ps1
```

构建产物：`EtherTransfer.Android/build/EtherTransfer-Android.apk`。开发签名密钥为 `build/development.p12`（已在 `.gitignore` 中排除，仅本地使用）。

真机联调（需设环境变量，否则自动跳过）：

```bash
ETHERTRANSFER_PEER_IP=192.168.1.20 ETHERTRANSFER_PEER_PORT=39000 \
  dotnet test EtherTransfer.Tests -c Release --filter LanSmokeTests
```

---

## 五、已知限制

- 传输时安卓端需保持应用在前台或已开启"后台常驻"；本版**没有断点续传**，中断需重传。
- 路由器开启 AP 隔离时，TCP 直连可能被拦，此时依赖 UDP `39000` 兜底通道（速度低于 TCP）。
- 有线直连依赖两端网卡自动协商链路本地地址；个别老网卡需在系统里确认"自动获取 IP"。
- 本次发布仅验证 Windows x64 与 Android；Linux 桌面代码保留但未随本次发布验证。

---

## 六、许可证

[MIT](LICENSE)。上游原作者：[Divy Viradiya](https://github.com/divyviradiya2)，本项目在其汉化版基础上完成中文本地化、双链路选路、设备互通巡检与安卓端实现。
