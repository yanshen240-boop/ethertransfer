using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using EtherTransfer.Core.Models;

namespace EtherTransfer.Services;

public static class FirewallHelper
{
    private const string RuleName = "EtherTransfer";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRuleConfiguredForPath(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{RuleName}\" verbose",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {

                return output.Contains(exePath, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {

        }

        return false;
    }

    public static void EnsureFirewallRule(Action<string, LogLevel>? logger = null, int tcpPort = 55000, int udpPort = 50000,
        int broadcastUdpPort = 39000, int broadcastProbePort = 39001)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (!IsAdministrator())
        {
            logger?.Invoke(
                "当前不是管理员身份，无法自动写入 Windows 防火墙规则。" +
                "如果其他设备能发现本机但无法传输（或根本发现不了），请用管理员权限执行一次：" +
                $"netsh advfirewall firewall add rule name=\"EtherTransfer-TCP\" dir=in action=allow protocol=TCP localport={tcpPort} profile=private,public 以及 " +
                $"netsh advfirewall firewall add rule name=\"EtherTransfer-UDP\" dir=in action=allow protocol=UDP localport={udpPort} profile=private,public " +
                $"以及 netsh advfirewall firewall add rule name=\"EtherTransfer-UDP-Broadcast\" dir=in action=allow protocol=UDP localport={broadcastUdpPort}-{broadcastProbePort} profile=private,public",
                LogLevel.Warning);

            return;
        }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                logger?.Invoke("无法解析用于注册防火墙规则的进程路径。", LogLevel.Warning);
                return;
            }

            if (IsRuleConfiguredForPath(exePath))
            {
                logger?.Invoke($"Windows 防火墙规则已生效：{exePath}", LogLevel.Info);
                EnsurePortRules(tcpPort, udpPort, logger);
                return;
            }

            logger?.Invoke($"正在为便携版程序注册 Windows 防火墙规则：{exePath}", LogLevel.Info);

            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exePath}\" enable=yes profile=private,public",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                if (process.WaitForExit(4000))
                {
                    if (process.ExitCode == 0)
                    {
                        logger?.Invoke("Windows 防火墙规则注册成功。", LogLevel.Info);
                    }
                    else
                    {
                        string err = process.StandardError.ReadToEnd();
                        logger?.Invoke($"防火墙规则注册进程已退出，代码 {process.ExitCode}：{err}", LogLevel.Warning);
                    }
                }
                else
                {
                    try { process.Kill(); } catch { }
                    logger?.Invoke("防火墙规则注册命令执行超时。", LogLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"注册防火墙规则时发生异常：{ex.Message}", LogLevel.Warning);
        }

        EnsurePortRules(tcpPort, udpPort, logger);
    }

    /// <summary>
    /// 补端口规则。程序路径规则一旦换了目录（重新解压、拷到另一台机器）就失效，
    /// 而端口规则与 exe 位置无关，是双机/多版本部署下唯一稳定的放行方式。
    /// </summary>
    private static void EnsurePortRules(int tcpPort, int udpPort, Action<string, LogLevel>? logger,
        int broadcastUdpPort = 39000, int broadcastProbePort = 39001)
    {
        AddPortRule("EtherTransfer-TCP", "TCP", tcpPort.ToString(), logger);
        AddPortRule("EtherTransfer-UDP", "UDP", udpPort.ToString(), logger);

        // AP 隔离模式下对端只发 UDP 广播过来；这两个端口不放行时，
        // “发现得到、传不了”会表现为完全静默（无 RST，只能等超时）。
        if (broadcastUdpPort > 0)
        {
            var range = broadcastProbePort > broadcastUdpPort
                ? $"{broadcastUdpPort}-{broadcastProbePort}"
                : broadcastUdpPort.ToString();
            AddPortRule("EtherTransfer-UDP-Broadcast", "UDP", range, logger);
        }
    }

    private static void AddPortRule(string name, string protocol, string ports, Action<string, LogLevel>? logger)
    {
        if (string.IsNullOrWhiteSpace(ports)) return;

        try
        {
            var check = RunNetsh($"advfirewall firewall show rule name=\"{name}\"", 3000);
            if (check.exitCode == 0 && !string.IsNullOrWhiteSpace(check.output))
            {
                return;
            }

            var add = RunNetsh(
                $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol={protocol} localport={ports} profile=private,public",
                4000);

            if (add.exitCode == 0)
                logger?.Invoke($"已添加防火墙端口规则：{name} {protocol}/{ports}", LogLevel.Info);
            else
                logger?.Invoke($"添加防火墙端口规则失败：{name} {protocol}/{ports} - {add.output}", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"添加防火墙端口规则异常：{name} - {ex.Message}", LogLevel.Warning);
        }
    }

    private static (int exitCode, string output) RunNetsh(string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null) return (-1, "无法启动 netsh");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch { }
            return (-1, "netsh 执行超时");
        }

        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }
}

