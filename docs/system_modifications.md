# 对系统的修改与幕后行为

> **在线文档**：带防火墙命令切换器和安全拆解的交互式网页版，见 **[安全与系统修改文档](https://divyviradiya2.github.io/ethertransfer/docs.html#security-sandboxing)**。

本文说明 EtherTransfer 在安装和运行期间对操作系统所做的全部修改。我们坚持完全透明，让管理员和开发者清楚知道程序改动了哪些网络绑定、防火墙规则和系统服务。

---

## 1. Windows 系统

### 安装过程（`EtherTransfer_Setup.exe`）
用户运行 Inno Setup 安装包后，会进行以下改动：
- **文件系统**：
  - 将程序文件解压到 `C:\Program Files\EtherTransfer`（32 位系统为 `C:\Program Files (x86)\EtherTransfer`）。
  - 在开始菜单和桌面创建快捷方式。
- **Windows Defender 防火墙**：
  - 安装程序会静默执行 `netsh` 命令，自动将本程序加入白名单。
  - `netsh advfirewall firewall add rule name="EtherTransfer" dir=in action=allow program="{app}\EtherTransfer.exe" enable=yes profile=private,public`
  - 这样可确保设备发现（UDP）与直连传输（TCP）不被 Windows 拦截。
- **卸载**：
  - 卸载程序会完整删除所有程序文件与快捷方式，并静默执行 `netsh advfirewall firewall delete rule` 移除防火墙白名单项。

### 运行时行为
- **网络套接字**：为设备发现打开 UDP 广播套接字；发起传输时绑定 TCP 监听套接字。
- **TCP Keep-Alive**：通过 Windows API 修改底层套接字选项，启用操作系统级 TCP Keep-Alive（用于在界面空闲等待时检测网线被物理拔掉）。

---

## 2. Linux 系统

### 安装过程（`install_linux.sh`）
通用 Linux 安装脚本需要 `sudo`（root）权限，并会进行较完整的环境准备：

- **文件系统**：
  - 下载并解压程序文件到 `/opt/ethertransfer`。
  - 下载程序图标到 `/usr/share/pixmaps/ethertransfer.ico`。
  - 在 `/usr/share/applications/ethertransfer.desktop` 创建标准桌面启动项，使其出现在应用菜单中（GNOME、KDE 等）。
  - 在 `/usr/local/bin/ethertransfer` 创建符号链接，便于在任意终端中直接启动。

- **动态防火墙配置**：
  脚本会检测当前使用的防火墙管理器，并放行 **端口 50000（UDP 设备发现）** 与 **端口 55000（TCP 文件传输）**，这是设备发现与高速直连所必需的。
  - 检测到 **UFW**（Ubuntu/Debian）：执行 `ufw allow 50000/udp` 与 `ufw allow 55000/tcp`。
  - 检测到 **Firewalld**（Fedora/RHEL/CentOS）：执行 `firewall-cmd --permanent --add-port=50000/udp` 与 `--add-port=55000/tcp`。
  - 检测到 **iptables**：直接向 INPUT 链追加 ACCEPT 规则。

- **依赖安装（NetworkManager）**：
  EtherTransfer 依赖 `nmcli` 进行底层网络接口监控。脚本会先检测该命令；若缺失，则自动识别系统的包管理器（`apt`、`dnf`、`pacman`、`zypper`、`yum`）来安装 `network-manager` 包，随后通过 `systemctl` 启用并启动该服务。

### 运行时行为
- **套接字绑定**：绑定 `0.0.0.0:50000` 用于 UDP 设备发现广播，绑定 `0.0.0.0:55000` 用于接收 TCP 传输流。
- **路径净化**：保存接收到的文件时，EtherTransfer 会对路径做严格净化，防止针对 Linux 文件系统的恶意目录穿越攻击（例如剥离 `../` 以免覆盖 `/etc/`、`/usr/` 下的文件）。

---

## 3. 配置与设置存储（全平台）

- 用户偏好（自定义设备名）保存在系统标准的用户应用数据目录中：
  - **Windows**：`%AppData%\EtherTransfer\settings.json`
  - **Linux**：`~/.config/EtherTransfer/settings.json`
- 安装版与便携版共用该位置，因此即使你把便携版程序在文件夹间移动，或用 U 盘启动，对端看到的设备身份依然保持不变。

---

## 4. 存储与临时数据（全平台）

- **临时缓冲**：传输过程中，EtherTransfer 采用"磁盘→网络"与"网络→磁盘"的纯流式处理。它使用有界的 32 MB `System.Threading.Channels` 双缓冲流水线，以及从 `ArrayPool<byte>.Shared` 租用的 128 KB 内存缓冲，避免内存膨胀，无论传输多大数据集，内存占用都被严格限制。
- **无遥测**：EtherTransfer 不会安装任何后台跟踪服务、遥测代理或开机分析钩子；只有用户主动启动时它才会运行。
