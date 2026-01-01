using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Reflection;

[InitializeOnLoad]
public class LogSync
{
    private static string logFilePath;
    private static int lastLogCount = 0;
    private static MethodInfo getCountMethod;

    static LogSync()
    {
        // 1. Setup unique paths for ParallelSync clones
        string projectPath = Directory.GetCurrentDirectory();
        string projectName = Path.GetFileName(projectPath);
        string logDir = Path.Combine(projectPath, "Logs");
        
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        logFilePath = Path.Combine(logDir, $"Console_{projectName}.log");

        // 2. Prepare Reflection for Console Count
        Assembly unityEditorAssembly = Assembly.GetAssembly(typeof(UnityEditor.Editor));
        Type logEntriesType = unityEditorAssembly.GetType("UnityEditor.LogEntries");
        getCountMethod = logEntriesType.GetMethod("GetCount");

        // 3. Initialize state
        Application.logMessageReceivedThreaded += OnLogReceived;
        EditorApplication.update += MonitorConsoleClear;
    }

    private static void MonitorConsoleClear()
    {
        // Get current console entry count via Reflection
        int currentCount = (int)getCountMethod.Invoke(null, null);

        // If count drops (User clicked Clear, or Clear on Play triggered)
        if (currentCount < lastLogCount && currentCount == 0)
        {
            File.WriteAllText(logFilePath, string.Empty);
        }
        
        lastLogCount = currentCount;
    }

    private static void OnLogReceived(string condition, string stackTrace, LogType type)
    {
        // Synchronous write to ensure order; use lock if high-frequency logging occurs
        try
        {
            using (StreamWriter sw = new StreamWriter(logFilePath, true))
            {
                sw.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{type}] {condition}");
                if (type == LogType.Exception || type == LogType.Error)
                {
                    sw.WriteLine(stackTrace);
                }
            }
        }
        catch (IOException) { /* File might be locked by external viewer */ }
    }
}