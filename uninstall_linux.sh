#!/bin/bash

RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
MAGENTA='\033[0;35m'
NC='\033[0m'
BOLD='\033[1m'
DIM='\033[2m'
GRAY='\033[0;90m'

SPINNER_FRAMES=("⠋" "⠙" "⠹" "⠸" "⠼" "⠴" "⠦" "⠧" "⠇" "⠏")
SPINNER_PID=""

cleanup() {
    stop_spinner
}
trap cleanup EXIT INT TERM

print_success() {
    echo -e "\r\033[K${GREEN}[✔]${NC} $1"
}

print_error() {
    echo -e "\r\033[K${RED}[✖]${NC} $1"
}

print_warning() {
    echo -e "\r\033[K${YELLOW}[!]${NC} $1"
}

start_spinner() {
    local msg="$1"
    (
        local i=0
        while true; do
            printf "\r\033[K    ${MAGENTA}%s${NC} ${DIM}%s${NC}" "${SPINNER_FRAMES[$i]}" "$msg"
            i=$(( (i + 1) % ${#SPINNER_FRAMES[@]} ))
            sleep 0.1
        done
    ) &
    SPINNER_PID=$!
    disown "$SPINNER_PID" 2>/dev/null
}

stop_spinner() {
    if [ -n "$SPINNER_PID" ]; then
        kill "$SPINNER_PID" 2>/dev/null
        wait "$SPINNER_PID" 2>/dev/null
        SPINNER_PID=""
    fi
    printf "\r\033[K"
}

clear

echo -e "${CYAN}${BOLD}"
echo "███████╗████████╗██╗  ██╗███████╗██████╗ ████████╗██████╗ █████╗ ███╗   ██╗███████╗███████╗███████╗██████╗"
echo "██╔════╝╚══██╔══╝██║  ██║██╔════╝██╔══██╗╚══██╔══╝██╔══██╗██╔══██╗████╗  ██║██╔════╝██╔════╝██╔════╝██╔══██╗"
echo "█████╗     ██║   ███████║█████╗  ██████╔╝   ██║   ██████╔╝███████║██╔██╗ ██║███████╗█████╗  █████╗  ██████╔╝"
echo "██╔══╝     ██║   ██╔══██║██╔══╝  ██╔══██╗   ██║   ██╔══██╗██╔══██║██║╚██╗██║╚════██║██╔══╝  ██╔══╝  ██╔══██╗"
echo "███████╗   ██║   ██║  ██║███████╗██║  ██║   ██║   ██║  ██║██║  ██║██║ ╚████║███████║██║     ███████╗██║  ██║"
echo "╚══════╝   ╚═╝   ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝   ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝╚══════╝╚═╝     ╚══════╝╚═╝  ╚═╝"
echo -e "${NC}"
echo -e "${BLUE}By DS Labs${NC} ${GRAY}•${NC} ${DIM}基于 MIT 许可开源${NC}\n"
echo -e "${YELLOW}卸载程序${NC}\n"

if [ "$EUID" -ne 0 ]; then
  echo -e "${YELLOW}卸载 EtherTransfer 需要管理员权限（sudo）。${NC}\n"
  echo "需要该权限用于："
  echo "  1. 从系统应用目录（/opt）中移除程序文件"
  echo "  2. 移除安装时创建的防火墙规则"
  echo "  3. 从应用菜单中移除程序快捷方式"
  echo "  4. 移除 ethertransfer 终端命令"
  echo ""
  echo -e "请改用以下命令重新运行卸载程序：${CYAN}sudo bash uninstall_linux.sh${NC}"
  exit 1
fi

INSTALL_DIR="/opt/ethertransfer"
DESKTOP_FILE="/usr/share/applications/ethertransfer.desktop"
ICON_PNG="/usr/share/pixmaps/ethertransfer.png"
ICON_ICO="/usr/share/pixmaps/ethertransfer.ico"
SYMLINK="/usr/local/bin/ethertransfer"

if [ ! -d "$INSTALL_DIR" ] && [ ! -f "$DESKTOP_FILE" ] && [ ! -L "$SYMLINK" ]; then
    print_warning "系统中似乎未安装 EtherTransfer。"
    echo ""
    exit 0
fi

echo -ne "${YELLOW}确定要卸载 EtherTransfer 吗？[y/N]：${NC}"
read -r CONFIRM < /dev/tty
if [[ ! "$CONFIRM" =~ ^[Yy]$ ]]; then
    echo -e "\n${RED}用户已取消卸载。${NC}\n"
    exit 0
fi
echo ""

start_spinner "正在移除防火墙规则..."

FIREWALL_REMOVED=false

if command -v ufw > /dev/null; then
    ufw delete allow 50000/udp > /dev/null 2>&1
    ufw delete allow 50000/tcp > /dev/null 2>&1
    ufw delete allow 55000/tcp > /dev/null 2>&1
    ufw delete allow 55000/udp > /dev/null 2>&1
    ufw delete allow 8840/tcp > /dev/null 2>&1
    ufw delete allow 8840/udp > /dev/null 2>&1
    FIREWALL_REMOVED=true
fi

if command -v firewall-cmd > /dev/null && firewall-cmd --state > /dev/null 2>&1; then
    firewall-cmd --permanent --remove-port=50000/udp > /dev/null 2>&1
    firewall-cmd --permanent --remove-port=50000/tcp > /dev/null 2>&1
    firewall-cmd --permanent --remove-port=55000/tcp > /dev/null 2>&1
    firewall-cmd --permanent --remove-port=55000/udp > /dev/null 2>&1
    firewall-cmd --permanent --remove-port=8840/tcp > /dev/null 2>&1
    firewall-cmd --permanent --remove-port=8840/udp > /dev/null 2>&1
    firewall-cmd --reload > /dev/null 2>&1
    FIREWALL_REMOVED=true
fi

if command -v iptables > /dev/null; then
    iptables -w -D INPUT -p udp --dport 50000 -j ACCEPT > /dev/null 2>&1 || iptables -D INPUT -p udp --dport 50000 -j ACCEPT > /dev/null 2>&1
    iptables -w -D INPUT -p tcp --dport 50000 -j ACCEPT > /dev/null 2>&1 || iptables -D INPUT -p tcp --dport 50000 -j ACCEPT > /dev/null 2>&1
    iptables -w -D INPUT -p tcp --dport 55000 -j ACCEPT > /dev/null 2>&1 || iptables -D INPUT -p tcp --dport 55000 -j ACCEPT > /dev/null 2>&1
    iptables -w -D INPUT -p udp --dport 55000 -j ACCEPT > /dev/null 2>&1 || iptables -D INPUT -p udp --dport 55000 -j ACCEPT > /dev/null 2>&1
    iptables -w -D INPUT -p tcp --dport 8840 -j ACCEPT > /dev/null 2>&1 || iptables -D INPUT -p tcp --dport 8840 -j ACCEPT > /dev/null 2>&1
    iptables -w -D INPUT -p udp --dport 8840 -j ACCEPT > /dev/null 2>&1 || iptables -D INPUT -p udp --dport 8840 -j ACCEPT > /dev/null 2>&1
    FIREWALL_REMOVED=true
fi

stop_spinner
if [ "$FIREWALL_REMOVED" = true ]; then
    print_success "防火墙规则已清除"
else
    print_warning "未检测到启用的防火墙，已跳过"
fi

start_spinner "正在移除桌面集成..."

[ -f "$DESKTOP_FILE" ] && rm -f "$DESKTOP_FILE"
[ -f "$ICON_PNG" ] && rm -f "$ICON_PNG"
[ -f "$ICON_ICO" ] && rm -f "$ICON_ICO"
[ -L "$SYMLINK" ] && rm -f "$SYMLINK"

if command -v update-desktop-database > /dev/null 2>&1; then
    update-desktop-database /usr/share/applications > /dev/null 2>&1
fi

stop_spinner
print_success "桌面快捷方式、图标与终端命令已移除"

if [ -d "$INSTALL_DIR" ]; then
    start_spinner "正在删除 $INSTALL_DIR..."
    rm -rf "$INSTALL_DIR"
    stop_spinner
    print_success "已删除 $INSTALL_DIR"
else
    print_warning "未找到 $INSTALL_DIR，已跳过"
fi

echo ""
echo -e "${GREEN}${BOLD}卸载完成${NC}"
echo "EtherTransfer 已从系统中完全移除。"
echo ""
