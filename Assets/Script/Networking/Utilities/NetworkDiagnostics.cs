using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Logging;

namespace YARG.Networking.Utilities
{
    /// <summary>
    /// Provides network diagnostic utilities for troubleshooting connectivity issues.
    /// </summary>
    public static class NetworkDiagnostics
    {
        /// <summary>
        /// Tests if a UDP port is reachable from outside the local network.
        /// This performs a loopback test through a public STUN server.
        /// </summary>
        public static async UniTask<DiagnosticResult> TestUdpPortReachabilityAsync(int port, int timeoutMs = 5000)
        {
            var result = new DiagnosticResult();
            result.Port = port;
            result.StartTime = DateTime.UtcNow;
            
            try
            {
                // Step 1: Test local UDP socket binding
                result.AddStep("Testing local UDP socket binding...");
                using var testSocket = new UdpClient();
                testSocket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                var localPort = ((IPEndPoint)testSocket.Client.LocalEndPoint!).Port;
                result.AddStep($"Successfully bound to UDP port {localPort}");
                result.LocalBindSuccess = true;
                
                // Step 2: Check Windows Firewall (Windows only)
#if UNITY_STANDALONE_WIN
                result.AddStep("Checking Windows Firewall rules...");
                var firewallResult = CheckWindowsFirewall(port);
                result.FirewallStatus = firewallResult;
                result.AddStep($"Firewall check: {firewallResult}");
#else
                result.FirewallStatus = "Skipped (not Windows)";
#endif
                
                // Step 3: Try to send/receive through STUN to test external reachability
                result.AddStep("Testing external reachability via STUN...");
                var stunResult = await TestStunReflexiveAsync(testSocket, timeoutMs);
                result.StunResult = stunResult;
                result.AddStep($"STUN test: {stunResult}");
                
                result.Success = result.LocalBindSuccess && !stunResult.Contains("failed");
            }
            catch (SocketException se)
            {
                result.AddStep($"Socket error: {se.SocketErrorCode} - {se.Message}");
                
                if (se.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    result.Error = $"Port {port} is already in use by another application.";
                }
                else if (se.SocketErrorCode == SocketError.AccessDenied)
                {
                    result.Error = $"Access denied when binding to port {port}. Try running as administrator.";
                }
                else
                {
                    result.Error = $"Socket error: {se.Message}";
                }
            }
            catch (Exception ex)
            {
                result.AddStep($"Error: {ex.Message}");
                result.Error = ex.Message;
            }
            
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            
            return result;
        }
        
#if UNITY_STANDALONE_WIN
        private static string CheckWindowsFirewall(int port)
        {
            try
            {
                // Check if there's an inbound rule for this port
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall show rule name=all dir=in | findstr /i \":{port}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return "Could not check firewall";
                
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                
                if (!string.IsNullOrEmpty(output))
                {
                    return $"Found rules mentioning port {port}";
                }
                
                // Check if there's a general rule for the application
                var appPath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(appPath))
                {
                    var psi2 = new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"advfirewall firewall show rule name=\"YARG\" dir=in",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    using var process2 = Process.Start(psi2);
                    if (process2 != null)
                    {
                        var output2 = process2.StandardOutput.ReadToEnd();
                        process2.WaitForExit(3000);
                        
                        if (output2.Contains("YARG"))
                        {
                            return "Found YARG firewall rule";
                        }
                    }
                }
                
                return "No specific rules found - firewall may block incoming UDP";
            }
            catch (Exception ex)
            {
                return $"Firewall check failed: {ex.Message}";
            }
        }
#endif
        
        private static async Task<string> TestStunReflexiveAsync(UdpClient client, int timeoutMs)
        {
            try
            {
                // Use Google's STUN server
                var stunEndpoint = new IPEndPoint(IPAddress.Parse("74.125.250.129"), 19302);
                
                // Build STUN binding request
                var stunRequest = new byte[20];
                stunRequest[0] = 0x00; // Binding Request
                stunRequest[1] = 0x01;
                stunRequest[2] = 0x00; // Message Length (0 for binding request)
                stunRequest[3] = 0x00;
                // Magic cookie
                stunRequest[4] = 0x21;
                stunRequest[5] = 0x12;
                stunRequest[6] = 0xA4;
                stunRequest[7] = 0x42;
                // Transaction ID (random)
                new System.Random().NextBytes(new Span<byte>(stunRequest, 8, 12));
                
                await client.SendAsync(stunRequest, stunRequest.Length, stunEndpoint);
                
                var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    var receiveTask = client.ReceiveAsync();
                    var timeoutTask = Task.Delay(timeoutMs, cts.Token);
                    
                    var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                    
                    if (completedTask == receiveTask)
                    {
                        var result = await receiveTask;
                        // Parse STUN response to get reflexive address
                        if (result.Buffer.Length >= 20 && result.Buffer[0] == 0x01 && result.Buffer[1] == 0x01)
                        {
                            // Success - STUN binding response received
                            // Parse XOR-MAPPED-ADDRESS attribute
                            var publicIp = ParseStunXorMappedAddress(result.Buffer);
                            if (publicIp != null)
                            {
                                return $"Success - Public endpoint: {publicIp}";
                            }
                            return "Success - Got STUN response (could not parse address)";
                        }
                        return "Received unexpected STUN response";
                    }
                    else
                    {
                        return "STUN request timed out - external connectivity may be blocked";
                    }
                }
                finally
                {
                    cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                return $"STUN test failed: {ex.Message}";
            }
        }
        
        private static IPEndPoint? ParseStunXorMappedAddress(byte[] data)
        {
            try
            {
                // STUN message header is 20 bytes
                int offset = 20;
                
                while (offset < data.Length - 4)
                {
                    ushort attrType = (ushort)((data[offset] << 8) | data[offset + 1]);
                    ushort attrLength = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                    offset += 4;
                    
                    // XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001)
                    if (attrType == 0x0020 && attrLength >= 8)
                    {
                        byte family = data[offset + 1];
                        if (family == 0x01) // IPv4
                        {
                            // XOR with magic cookie
                            ushort port = (ushort)(((data[offset + 2] << 8) | data[offset + 3]) ^ 0x2112);
                            uint ip = (uint)(
                                ((data[offset + 4] ^ 0x21) << 24) |
                                ((data[offset + 5] ^ 0x12) << 16) |
                                ((data[offset + 6] ^ 0xA4) << 8) |
                                (data[offset + 7] ^ 0x42)
                            );
                            
                            var ipAddress = new IPAddress(new byte[] {
                                (byte)((ip >> 24) & 0xFF),
                                (byte)((ip >> 16) & 0xFF),
                                (byte)((ip >> 8) & 0xFF),
                                (byte)(ip & 0xFF)
                            });
                            
                            return new IPEndPoint(ipAddress, port);
                        }
                    }
                    else if (attrType == 0x0001 && attrLength >= 8)
                    {
                        byte family = data[offset + 1];
                        if (family == 0x01) // IPv4
                        {
                            ushort port = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                            var ipAddress = new IPAddress(new byte[] {
                                data[offset + 4],
                                data[offset + 5],
                                data[offset + 6],
                                data[offset + 7]
                            });
                            
                            return new IPEndPoint(ipAddress, port);
                        }
                    }
                    
                    // Move to next attribute (pad to 4 bytes)
                    offset += attrLength + (4 - attrLength % 4) % 4;
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Tests if a remote endpoint is reachable via UDP.
        /// Sends a test packet and waits for any response.
        /// </summary>
        public static async UniTask<bool> TestUdpReachabilityAsync(string host, int port, int timeoutMs = 3000)
        {
            try
            {
                using var client = new UdpClient();
                var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
                
                // Send a test packet
                var testData = System.Text.Encoding.UTF8.GetBytes("YARG_UDP_TEST");
                await client.SendAsync(testData, testData.Length, endpoint);
                
                // Wait for response
                var cts = new CancellationTokenSource(timeoutMs);
                var receiveTask = client.ReceiveAsync();
                var timeoutTask = Task.Delay(timeoutMs, cts.Token);
                
                var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                cts.Cancel();
                
                return completedTask == receiveTask;
            }
            catch
            {
                return false;
            }
        }
    }
    
    /// <summary>
    /// Results from a network diagnostic test.
    /// </summary>
    public class DiagnosticResult
    {
        public int Port { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public bool LocalBindSuccess { get; set; }
        public string FirewallStatus { get; set; } = "Not checked";
        public string StunResult { get; set; } = "Not tested";
        public string? Error { get; set; }
        
        private readonly System.Collections.Generic.List<string> _steps = new();
        
        public void AddStep(string step)
        {
            _steps.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {step}");
            YargLogger.LogInfo($"[NetworkDiagnostics] {step}");
        }
        
        public string GetFullReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Network Diagnostic Report ===");
            sb.AppendLine($"Port: {Port}");
            sb.AppendLine($"Duration: {Duration.TotalMilliseconds:F0}ms");
            sb.AppendLine($"Success: {Success}");
            sb.AppendLine();
            sb.AppendLine("Steps:");
            foreach (var step in _steps)
            {
                sb.AppendLine($"  {step}");
            }
            sb.AppendLine();
            sb.AppendLine($"Local Bind: {(LocalBindSuccess ? "Success" : "Failed")}");
            sb.AppendLine($"Firewall: {FirewallStatus}");
            sb.AppendLine($"STUN: {StunResult}");
            if (!string.IsNullOrEmpty(Error))
            {
                sb.AppendLine($"Error: {Error}");
            }
            return sb.ToString();
        }
    }
}
