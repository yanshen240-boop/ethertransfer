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

BAR_FILL="━"
BAR_EMPTY="─"
BAR_HEAD="▶"
SPINNER_FRAMES=("⠋" "⠙" "⠹" "⠸" "⠼" "⠴" "⠦" "⠧" "⠇" "⠏")
SPINNER_PID=""
TMP_DIR=""

cleanup() {
    stop_spinner
    if [ -n "$TMP_DIR" ] && [ -d "$TMP_DIR" ]; then
        rm -rf "$TMP_DIR"
    fi
}
trap cleanup EXIT INT TERM

print_step() {
    echo -ne "\r\033[K${BLUE}[*]${NC} $1"
}

print_success() {
    echo -e "\r\033[K${GREEN}[✔]${NC} $1"
}

print_error() {
    echo -e "\r\033[K${RED}[✖]${NC} $1"
}

print_warning() {
    echo -e "\r\033[K${YELLOW}[!]${NC} $1"
}

format_bytes() {
    local bytes="${1:-0}"
    if ! [[ "$bytes" =~ ^[0-9]+$ ]]; then
        bytes=0
    fi
    if [ "$bytes" -ge 1073741824 ] 2>/dev/null; then
        awk "BEGIN{printf \"%.1f GB\", $bytes/1073741824}"
    elif [ "$bytes" -ge 1048576 ] 2>/dev/null; then
        awk "BEGIN{printf \"%.1f MB\", $bytes/1048576}"
    elif [ "$bytes" -ge 1024 ] 2>/dev/null; then
        awk "BEGIN{printf \"%.1f KB\", $bytes/1024}"
    else
        printf "%d B" "$bytes"
    fi
}

format_time() {
    local secs="${1:-0}"
    if ! [[ "$secs" =~ ^[0-9]+$ ]]; then
        secs=0
    fi
    if [ "$secs" -ge 3600 ] 2>/dev/null; then
        printf "%dh %dm" $((secs/3600)) $((secs%3600/60))
    elif [ "$secs" -ge 60 ] 2>/dev/null; then
        printf "%dm %ds" $((secs/60)) $((secs%60))
    else
        printf "%ds" "$secs"
    fi
}

draw_progress() {
    local percent=$1
    local downloaded=$2
    local total=$3
    local speed=$4
    local eta=$5

    local bar_width=30
    local filled=$((percent * bar_width / 100))
    local empty=$((bar_width - filled))

    local bar=""
    if [ "$filled" -gt 0 ]; then
        if [ "$filled" -ge "$bar_width" ]; then
            bar=$(printf "${BAR_FILL}%.0s" $(seq 1 $bar_width))
        else
            bar=$(printf "${BAR_FILL}%.0s" $(seq 1 $filled))
            bar="${bar}${BAR_HEAD}"
            if [ "$empty" -gt 1 ]; then
                bar="${bar}$(printf "${BAR_EMPTY}%.0s" $(seq 1 $((empty - 1))))"
            fi
        fi
    else
        bar=$(printf "${BAR_EMPTY}%.0s" $(seq 1 $bar_width))
    fi

    local bar_color="${BLUE}"
    if [ "$percent" -ge 100 ]; then
        bar_color="${GREEN}"
    elif [ "$percent" -ge 75 ]; then
        bar_color="${CYAN}"
    elif [ "$percent" -ge 50 ]; then
        bar_color="${BLUE}"
    fi

    local dl_str=$(format_bytes "$downloaded")
    local total_str=$(format_bytes "$total")
    local speed_str="$(format_bytes "$speed")/s"
    local eta_str=""
    if [ "$eta" -gt 0 ] 2>/dev/null && [ "$percent" -lt 100 ]; then
        eta_str="ETA $(format_time "$eta")"
    elif [ "$percent" -ge 100 ]; then
        eta_str="Done!"
    fi

    printf "\r    ${GRAY}│${NC} ${bar_color}${bar}${NC} ${BOLD}%3d%%${NC} ${GRAY}│${NC} ${DIM}%s / %s${NC} ${GRAY}│${NC} ${DIM}%s${NC} ${GRAY}│${NC} ${DIM}%s${NC}   " \
        "$percent" "$dl_str" "$total_str" "$speed_str" "$eta_str"
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

get_file_size() {
    local file="$1"
    if [ -f "$file" ]; then
        wc -c < "$file" 2>/dev/null | tr -d ' ' || echo 0
    else
        echo 0
    fi
}

download_with_progress() {
    local url="$1"
    local output="$2"

    local total_size=0
    if command -v curl > /dev/null; then
        total_size=$(curl -sIL --connect-timeout 10 "$url" 2>/dev/null | grep -i '^content-length:' | tail -1 | tr -d '[:space:]' | cut -d: -f2 | tr -d '\r')
    fi
    total_size=${total_size:-0}
    if ! [[ "$total_size" =~ ^[0-9]+$ ]] || [ "$total_size" -le 0 ]; then
        total_size=0
    fi

    if command -v curl > /dev/null; then
        if [ "$total_size" -gt 0 ]; then
            curl -L --connect-timeout 15 -o "$output" "$url" 2>/dev/null &
            local curl_pid=$!
            local start_time=$(date +%s)

            while kill -0 "$curl_pid" 2>/dev/null; do
                if [ -f "$output" ]; then
                    local current_size=$(get_file_size "$output")
                    local now=$(date +%s)
                    local elapsed=$((now - start_time))
                    local speed=0
                    if [ "$elapsed" -gt 0 ]; then
                        speed=$((current_size / elapsed))
                    fi
                    local percent=0
                    if [ "$total_size" -gt 0 ]; then
                        percent=$((current_size * 100 / total_size))
                        [ "$percent" -gt 100 ] && percent=100
                    fi
                    local eta=0
                    if [ "$speed" -gt 0 ] && [ "$total_size" -gt 0 ]; then
                        eta=$(( (total_size - current_size) / speed ))
                    fi
                    draw_progress "$percent" "$current_size" "$total_size" "$speed" "$eta"
                fi
                sleep 0.3
            done

            wait "$curl_pid"
            local exit_code=$?

            if [ $exit_code -eq 0 ] && [ -s "$output" ]; then
                local final_size=$(get_file_size "$output")
                local now=$(date +%s)
                local elapsed=$((now - start_time))
                local speed=0
                if [ "$elapsed" -gt 0 ]; then
                    speed=$((final_size / elapsed))
                fi
                draw_progress 100 "$final_size" "$total_size" "$speed" 0
                sleep 0.5
                printf "\r\033[K"
            fi

            return $exit_code
        else
            start_spinner "正在下载..."
            curl -L --connect-timeout 15 -o "$output" "$url" 2>/dev/null
            local exit_code=$?
            stop_spinner
            return $exit_code
        fi
    elif command -v wget > /dev/null; then
        start_spinner "正在下载..."
        wget -q --timeout=15 --tries=1 "$url" -O "$output" 2>/dev/null
        local exit_code=$?
        stop_spinner
        return $exit_code
    else
        print_error "Neither curl nor wget is installed. Cannot download."
        return 1
    fi
}

install_package() {
    local pkg_deb="$1"
    local pkg_rpm="$2"
    local pkg_arch="$3"
    local pkg_suse="$4"
    local pkg_apk="$5"

    if command -v apt-get > /dev/null; then
        apt-get update -qq > /dev/null 2>&1
        apt-get install -y -qq "$pkg_deb" > /dev/null 2>&1
    elif command -v dnf > /dev/null; then
        dnf install -y -q "$pkg_rpm" > /dev/null 2>&1
    elif command -v pacman > /dev/null; then
        pacman -S --noconfirm --quiet "$pkg_arch" > /dev/null 2>&1
    elif command -v zypper > /dev/null; then
        zypper install -y --quiet "$pkg_suse" > /dev/null 2>&1
    elif command -v apk > /dev/null; then
        apk add --quiet "$pkg_apk" > /dev/null 2>&1
    elif command -v yum > /dev/null; then
        yum install -y -q "$pkg_rpm" > /dev/null 2>&1
    elif command -v xbps-install > /dev/null; then
        xbps-install -y -q "$pkg_rpm" > /dev/null 2>&1
    fi
}

extract_archive() {
    local archive="$1"
    local dest="$2"

    if command -v unzip > /dev/null; then
        unzip -o -q "$archive" -d "$dest"
        return $?
    elif command -v python3 > /dev/null; then
        python3 -m zipfile -e "$archive" "$dest" > /dev/null 2>&1
        return $?
    elif command -v python > /dev/null; then
        python -m zipfile -e "$archive" "$dest" > /dev/null 2>&1
        return $?
    elif command -v bsdtar > /dev/null; then
        bsdtar -xf "$archive" -C "$dest" > /dev/null 2>&1
        return $?
    elif command -v tar > /dev/null; then
        tar -xf "$archive" -C "$dest" > /dev/null 2>&1
        return $?
    elif command -v 7z > /dev/null || command -v 7za > /dev/null; then
        local zcmd="7z"
        command -v 7za > /dev/null && zcmd="7za"
        $zcmd x -y -o"$dest" "$archive" > /dev/null 2>&1
        return $?
    else
        install_package "unzip" "unzip" "unzip" "unzip" "unzip"
        if command -v unzip > /dev/null; then
            unzip -o -q "$archive" -d "$dest"
            return $?
        fi
        return 1
    fi
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

if [ "$EUID" -ne 0 ]; then
  echo -e "${YELLOW}安装 EtherTransfer 需要管理员权限（sudo）。${NC}\n"
  echo "需要该权限用于："
  echo "  1. 将程序文件安装到系统应用目录（/opt）"
  echo "  2. 配置防火墙，允许直接文件传输"
  echo "  3. 在应用菜单中添加程序快捷方式"
  echo "  4. 创建 ethertransfer 终端命令"
  echo ""
  echo -e "请改用以下命令重新运行安装程序：${CYAN}sudo bash install_linux.sh${NC}"
  exit 1
fi

echo -ne "\n${YELLOW}是否继续安装 EtherTransfer？[y/N]：${NC}"
read -r CONFIRM < /dev/tty
if [[ ! "$CONFIRM" =~ ^[Yy]$ ]]; then
    echo -e "\n${RED}用户已取消安装。${NC}\n"
    exit 0
fi
echo ""

print_step "正在检查系统架构..."
ARCH=$(uname -m)
if [ "$ARCH" = "x86_64" ] || [ "$ARCH" = "amd64" ]; then
    ET_ARCH="x64"
    print_success "架构 $ARCH 受支持"
else
    print_error "不支持的架构：$ARCH，仅支持 x86_64。"
    exit 1
fi

INSTALL_DIR="/opt/ethertransfer"

SKIP_DOWNLOAD=false
if [ -d "$INSTALL_DIR" ] && [ -f "$INSTALL_DIR/EtherTransfer" ]; then
    echo -ne "\n${YELLOW}检测到 EtherTransfer 已安装，是否更新/重新安装程序文件？[y/N]：${NC}"
    read -r UPDATE_CONFIRM < /dev/tty
    if [[ "$UPDATE_CONFIRM" =~ ^[Yy]$ ]]; then
        SKIP_DOWNLOAD=false
        echo ""
    else
        print_success "已跳过程序文件更新（跳过）"
        SKIP_DOWNLOAD=true
    fi
fi

if [ "$SKIP_DOWNLOAD" = false ]; then
    TMP_DIR=$(mktemp -d 2>/dev/null || mktemp -d -t 'et_tmp.XXXXXXXXXX')
    DOWNLOAD_URL="https://github.com/divyviradiya2/ethertransfer/releases/latest/download/EtherTransfer-linux-${ET_ARCH}.zip"

    MAX_RETRIES=3
    RETRY_COUNT=0
    DOWNLOAD_SUCCESS=false

    while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
        download_with_progress "$DOWNLOAD_URL" "$TMP_DIR/ethertransfer.zip"

        if [ -s "$TMP_DIR/ethertransfer.zip" ]; then
            DOWNLOAD_SUCCESS=true
            dl_size=$(get_file_size "$TMP_DIR/ethertransfer.zip")
            print_success "下载完成（$(format_bytes "$dl_size")）"
            break
        else
            RETRY_COUNT=$((RETRY_COUNT+1))
            print_warning "下载失败，正在重试（$RETRY_COUNT/$MAX_RETRIES）..."
            sleep 2
        fi
    done

    if [ "$DOWNLOAD_SUCCESS" = false ]; then
        print_error "多次重试后仍未成功下载 EtherTransfer。"
        exit 1
    fi

    print_step "正在解压文件到 $INSTALL_DIR..."
    mkdir -p "$INSTALL_DIR"

    start_spinner "正在解压..."
    if extract_archive "$TMP_DIR/ethertransfer.zip" "$INSTALL_DIR"; then
        stop_spinner
    else
        stop_spinner
        print_error "无法解压安装包，请确认已安装 unzip、tar 或 python3。"
        exit 1
    fi

    if [ ! -f "$INSTALL_DIR/EtherTransfer" ]; then
        NESTED_DIR=$(find "$INSTALL_DIR" -mindepth 1 -maxdepth 2 -type f -name "EtherTransfer" -exec dirname {} \; 2>/dev/null | head -n 1)
        if [ -n "$NESTED_DIR" ] && [ "$NESTED_DIR" != "$INSTALL_DIR" ]; then
            mv "$NESTED_DIR"/* "$INSTALL_DIR/" 2>/dev/null
            rmdir "$NESTED_DIR" 2>/dev/null || true
        fi
    fi

    if [ -f "$INSTALL_DIR/EtherTransfer" ]; then
        chmod +x "$INSTALL_DIR/EtherTransfer"
        print_success "文件解压完成"
    else
        print_error "安装包中未找到 EtherTransfer 可执行文件。"
        exit 1
    fi

    rm -rf "$TMP_DIR"
    TMP_DIR=""
fi

print_step "正在配置防火墙..."
FIREWALL_CONFIGURED=false

if command -v ufw > /dev/null; then
    if (ufw status 2>/dev/null | grep -q "50000" || ufw show added 2>/dev/null | grep -q "50000") && \
       (ufw status 2>/dev/null | grep -q "55000" || ufw show added 2>/dev/null | grep -q "55000"); then
        print_success "UFW 规则已存在（跳过）"
    else
        ufw allow 50000/udp > /dev/null 2>&1
        ufw allow 50000/tcp > /dev/null 2>&1
        ufw allow 55000/tcp > /dev/null 2>&1
        ufw allow 55000/udp > /dev/null 2>&1
        print_success "已为端口 50000（设备发现）和 55000（传输）添加 UFW 规则"
    fi
    FIREWALL_CONFIGURED=true
elif command -v firewall-cmd > /dev/null && firewall-cmd --state > /dev/null 2>&1; then
    if firewall-cmd --query-port=50000/udp > /dev/null 2>&1 && firewall-cmd --query-port=55000/tcp > /dev/null 2>&1; then
        print_success "Firewalld 规则已存在（跳过）"
    else
        firewall-cmd --permanent --add-port=50000/udp > /dev/null 2>&1
        firewall-cmd --permanent --add-port=50000/tcp > /dev/null 2>&1
        firewall-cmd --permanent --add-port=55000/tcp > /dev/null 2>&1
        firewall-cmd --permanent --add-port=55000/udp > /dev/null 2>&1
        firewall-cmd --reload > /dev/null 2>&1
        print_success "已为端口 50000（设备发现）和 55000（传输）添加 Firewalld 规则"
    fi
    FIREWALL_CONFIGURED=true
elif command -v iptables > /dev/null; then
    if iptables -C INPUT -p udp --dport 50000 -j ACCEPT >/dev/null 2>&1 && iptables -C INPUT -p tcp --dport 55000 -j ACCEPT >/dev/null 2>&1; then
        print_success "iptables 规则已存在（跳过）"
    else
        iptables -w -A INPUT -p udp --dport 50000 -j ACCEPT 2>/dev/null || iptables -A INPUT -p udp --dport 50000 -j ACCEPT > /dev/null 2>&1
        iptables -w -A INPUT -p tcp --dport 50000 -j ACCEPT 2>/dev/null || iptables -A INPUT -p tcp --dport 50000 -j ACCEPT > /dev/null 2>&1
        iptables -w -A INPUT -p tcp --dport 55000 -j ACCEPT 2>/dev/null || iptables -A INPUT -p tcp --dport 55000 -j ACCEPT > /dev/null 2>&1
        iptables -w -A INPUT -p udp --dport 55000 -j ACCEPT 2>/dev/null || iptables -A INPUT -p udp --dport 55000 -j ACCEPT > /dev/null 2>&1
        print_success "已为端口 50000（设备发现）和 55000（传输）添加 iptables 规则"
    fi
    FIREWALL_CONFIGURED=true
fi

if [ "$FIREWALL_CONFIGURED" = false ]; then
    print_warning "未检测到启用的防火墙，跳过防火墙配置"
fi

print_step "正在检查 NetworkManager..."
if command -v nmcli > /dev/null; then
    print_success "NetworkManager 已安装（跳过）"
else
    start_spinner "正在安装 NetworkManager..."
    install_package "network-manager" "NetworkManager" "networkmanager" "NetworkManager" "networkmanager"

    if command -v systemctl > /dev/null; then
        systemctl enable NetworkManager > /dev/null 2>&1
        systemctl start NetworkManager > /dev/null 2>&1
    elif command -v rc-service > /dev/null; then
        rc-update add networkmanager default > /dev/null 2>&1
        rc-service networkmanager start > /dev/null 2>&1
    elif command -v service > /dev/null; then
        service NetworkManager start > /dev/null 2>&1 || service network-manager start > /dev/null 2>&1
    fi
    stop_spinner

    if command -v nmcli > /dev/null; then
        print_success "NetworkManager 已安装并启动"
    else
        print_warning "无法自动安装 NetworkManager，请手动安装"
    fi
fi

print_step "正在配置桌面集成..."
ICON_DIR="/usr/share/pixmaps"
ICON_PNG_URL="https://raw.githubusercontent.com/divyviradiya2/ethertransfer/master/EtherTransfer.UI/Assets/logo.png"
DESKTOP_FILE="/usr/share/applications/ethertransfer.desktop"
SYMLINK="/usr/local/bin/ethertransfer"

mkdir -p "$ICON_DIR" "/usr/share/applications" "/usr/local/bin"

if [ -f "$ICON_DIR/ethertransfer.png" ] && [ -s "$ICON_DIR/ethertransfer.png" ]; then
    print_success "图标已存在（跳过）"
else
    start_spinner "正在下载图标..."
    if command -v curl > /dev/null; then
        curl -sSL "$ICON_PNG_URL" -o "$ICON_DIR/ethertransfer.png"
    elif command -v wget > /dev/null; then
        wget -q "$ICON_PNG_URL" -O "$ICON_DIR/ethertransfer.png"
    fi
    stop_spinner
    if [ -f "$ICON_DIR/ethertransfer.png" ] && [ -s "$ICON_DIR/ethertransfer.png" ]; then
        print_success "图标下载完成"
    else
        print_warning "无法下载图标（跳过）"
    fi
fi

if [ -f "$DESKTOP_FILE" ] && grep -q "Exec=$INSTALL_DIR/EtherTransfer" "$DESKTOP_FILE"; then
    print_success "桌面快捷方式已存在（跳过）"
else
    cat << EOF > "$DESKTOP_FILE"
[Desktop Entry]
Name=EtherTransfer 以太传输
Comment=DS Labs 出品的局域网文件传输工具
Exec=$INSTALL_DIR/EtherTransfer
Icon=/usr/share/pixmaps/ethertransfer.png
Terminal=false
Type=Application
Categories=Network;FileTransfer;Utility;
Keywords=ethernet;transfer;file;network;lan;
StartupNotify=true
EOF
    chmod 644 "$DESKTOP_FILE"
    print_success "桌面快捷方式已创建"
fi

if command -v update-desktop-database > /dev/null 2>&1; then
    update-desktop-database /usr/share/applications > /dev/null 2>&1
fi

if [ -L "$SYMLINK" ] && [ "$(readlink "$SYMLINK" 2>/dev/null)" = "$INSTALL_DIR/EtherTransfer" ]; then
    print_success "终端命令已存在（跳过）"
else
    ln -sf "$INSTALL_DIR/EtherTransfer" "$SYMLINK"
    print_success "终端命令已创建"
fi

echo ""
echo -e "${GREEN}${BOLD}安装完成${NC}"
echo -e "你可以从应用菜单启动 EtherTransfer，或在终端中运行 ${CYAN}ethertransfer${NC}。"
echo ""
