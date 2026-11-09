[Setup]
; 应用信息
AppName=EtherTransfer 以太传输
AppVersion=0.7.0
AppVerName=EtherTransfer 以太传输 0.7.0
AppPublisher=DS Labs
UninstallDisplayIcon={app}\EtherTransfer.exe

; 安装目录
DefaultDirName={autopf}\EtherTransfer
DefaultGroupName=EtherTransfer
DisableProgramGroupPage=yes

; 输出设置
OutputDir=.\publish\installer
OutputBaseFilename=EtherTransfer_Setup_x64
SetupIconFile=.\EtherTransfer.UI\Assets\logo.ico

; 最高压缩率设置
Compression=lzma2/ultra64
SolidCompression=yes
LZMAAlgorithm=1

; 架构（仅 64 位）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 安装到 Program Files 需要管理员权限
PrivilegesRequired=admin

[Languages]
Name: "chinesesimplified"; MessagesFile: "installer\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 主程序
Source: "publish\win-x64\EtherTransfer.exe"; DestDir: "{app}"; Flags: ignoreversion
; 其余 DLL 与运行时文件（不含调试符号）
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
; 开始菜单图标
Name: "{group}\EtherTransfer"; Filename: "{app}\EtherTransfer.exe"
; 桌面图标
Name: "{autodesktop}\EtherTransfer"; Filename: "{app}\EtherTransfer.exe"; Tasks: desktopicon

[Run]
; 为专用/公用网络添加防火墙规则（静默执行）
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""EtherTransfer"" dir=in action=allow program=""{app}\EtherTransfer.exe"" enable=yes profile=private,public"; Flags: runhidden
; 安装完成后启动程序
Filename: "{app}\EtherTransfer.exe"; Description: "{cm:LaunchProgram,EtherTransfer}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时删除防火墙规则（静默执行）
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""EtherTransfer"" program=""{app}\EtherTransfer.exe"""; Flags: runhidden
