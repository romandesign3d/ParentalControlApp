using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32.TaskScheduler;

public class Guardian
{
    private const string MainAppProcessName = "ParentalControlApp";
    private const string MainAppExeName = "ParentalControlApp.exe";
    private const string TaskName = "ParentalControlAppStartup";
    private static string _appDirectory = ""; 
    private static string _logFilePath = ""; 

    private static readonly object _lock = new object();

    private static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
                File.AppendAllText(_logFilePath, logMessage);
            }
        }
        catch {}
    }

    public static void Main(string[] args)
    {
        // Определяем папку приложения из аргументов командной строки
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            _appDirectory = args[0];
        }
        else
        {
            // Запасной вариант, если аргумент не передан
            _appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }
        _logFilePath = Path.Combine(_appDirectory, "guardian_log.txt");

        Log("Guardian process started.");
        Log($"Working directory set to: {_appDirectory}");
        
        while (true)
        {
            try
            {
                Log("Checking status...");

                // 1. Проверяем, запущен ли основной процесс
                if (Process.GetProcessesByName(MainAppProcessName).Length == 0)
                {
                    Log("Main app process not found. Relaunching...");
                    string mainAppPath = Path.Combine(_appDirectory, MainAppExeName);
                    if (File.Exists(mainAppPath))
                    {
                        Process.Start(new ProcessStartInfo(mainAppPath)
                        {
                            UseShellExecute = true,
                            Verb = "runas" 
                        });
                        Log("Main app relaunch command sent.");
                    }
                    else
                    {
                        Log($"CRITICAL: {MainAppExeName} not found at {mainAppPath}");
                    }
                }
                else
                {
                    Log("Main app process is running.");
                }

                // 2. Проверяем, существует ли задача в Планировщике
                using (TaskService ts = new TaskService())
                {
                    if (ts.GetTask(TaskName) == null)
                    {
                        Log("Scheduler task not found. Recreating...");
                        CreateSchedulerTask(ts);
                    }
                    else
                    {
                         Log("Scheduler task exists.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"An error occurred in guardian loop: {ex.ToString()}");
            }
            
            Thread.Sleep(10000); 
        }
    }

    private static void CreateSchedulerTask(TaskService ts)
    {
        try
        {
            TaskDefinition td = ts.NewTask();
            td.RegistrationInfo.Description = "Starts Parental Control application at user logon.";
            td.Principal.RunLevel = TaskRunLevel.Highest; 

            td.Triggers.Add(new LogonTrigger());

            string mainAppPath = Path.Combine(_appDirectory, MainAppExeName);
            
            // Создаем действие (ExecAction), чтобы можно было установить рабочую папку
            ExecAction action = new ExecAction(mainAppPath);
            action.WorkingDirectory = _appDirectory; // Устанавливаем свойство
            
            td.Actions.Add(action); // Добавляем уже настроенное действие

            ts.RootFolder.RegisterTaskDefinition(TaskName, td);
            Log("Scheduler task successfully recreated.");
        }
        catch (Exception ex)
        {
            Log($"Failed to create scheduler task: {ex.ToString()}");
        }
    }
}
