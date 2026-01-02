using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using YARG.Core.Logging;

namespace YARG.Networking.Utilities
{
    /// <summary>
    /// Manages Windows Firewall rules for YARG networking.
    /// </summary>
    public static class FirewallHelper
    {
#if UNITY_STANDALONE_WIN
        /// <summary>
        /// Checks if a firewall rule exists for the specified port.
        /// </summary>
        public static bool RuleExists(string ruleName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall show rule name=\"{ruleName}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return false;
                
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                
                return output.Contains(ruleName) && !output.Contains("No rules match the specified criteria");
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[FirewallHelper] Error checking firewall rule: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Creates an inbound UDP firewall rule for YARG.
        /// Requires administrator privileges.
        /// </summary>
        public static FirewallRuleResult CreateInboundUdpRule(string ruleName, int port)
        {
            try
            {
                // First check if rule already exists
                if (RuleExists(ruleName))
                {
                    YargLogger.LogInfo($"[FirewallHelper] Rule '{ruleName}' already exists");
                    return new FirewallRuleResult { Success = true, Message = "Rule already exists" };
                }
                
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=UDP localport={port}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Verb = "runas" // Request elevation
                };
                
                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new FirewallRuleResult { Success = false, Message = "Failed to start netsh process" };
                }
                
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
                
                if (process.ExitCode == 0 && output.Contains("Ok"))
                {
                    YargLogger.LogInfo($"[FirewallHelper] Successfully created firewall rule '{ruleName}' for UDP port {port}");
                    return new FirewallRuleResult { Success = true, Message = "Rule created successfully" };
                }
                else
                {
                    var message = !string.IsNullOrEmpty(error) ? error : output;
                    YargLogger.LogWarning($"[FirewallHelper] Failed to create rule: {message}");
                    return new FirewallRuleResult { Success = false, Message = message };
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740) // ERROR_ELEVATION_REQUIRED
            {
                return new FirewallRuleResult 
                { 
                    Success = false, 
                    Message = "Administrator privileges required. Please run YARG as administrator to create firewall rules.",
                    RequiresElevation = true
                };
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"[FirewallHelper] Error creating firewall rule: {ex.Message}");
                return new FirewallRuleResult { Success = false, Message = ex.Message };
            }
        }
        
        /// <summary>
        /// Creates inbound and outbound firewall rules for the YARG executable.
        /// This is the preferred method as it allows all ports for the application.
        /// </summary>
        public static FirewallRuleResult CreateApplicationRule()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    return new FirewallRuleResult { Success = false, Message = "Could not determine executable path" };
                }
                
                // Create inbound rule
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"YARG\" dir=in action=allow program=\"{exePath}\" enable=yes protocol=UDP",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new FirewallRuleResult { Success = false, Message = "Failed to start netsh process" };
                }
                
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);
                
                if (process.ExitCode == 0)
                {
                    YargLogger.LogInfo("[FirewallHelper] Successfully created YARG application firewall rule");
                    return new FirewallRuleResult { Success = true, Message = "Application firewall rule created" };
                }
                else
                {
                    return new FirewallRuleResult { Success = false, Message = output };
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogError($"[FirewallHelper] Error creating application rule: {ex.Message}");
                return new FirewallRuleResult { Success = false, Message = ex.Message };
            }
        }
        
        /// <summary>
        /// Opens Windows Firewall settings.
        /// </summary>
        public static void OpenFirewallSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "wf.msc",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"[FirewallHelper] Could not open firewall settings: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets information about current firewall state.
        /// </summary>
        public static string GetFirewallInfo()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall show currentprofile",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return "Could not get firewall info";
                
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                
                return output;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
#else
        public static bool RuleExists(string ruleName) => false;
        public static FirewallRuleResult CreateInboundUdpRule(string ruleName, int port) 
            => new FirewallRuleResult { Success = false, Message = "Only supported on Windows" };
        public static FirewallRuleResult CreateApplicationRule()
            => new FirewallRuleResult { Success = false, Message = "Only supported on Windows" };
        public static void OpenFirewallSettings() { }
        public static string GetFirewallInfo() => "Only supported on Windows";
#endif
    }
    
    /// <summary>
    /// Result of a firewall rule operation.
    /// </summary>
    public class FirewallRuleResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool RequiresElevation { get; set; }
    }
}
