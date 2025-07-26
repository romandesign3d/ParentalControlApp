using Microsoft.Win32.TaskScheduler;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ParentalControlApp
{
    public static class Logger
    {
        private static readonly string logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error_log.txt");
        private static readonly object _lock = new object();

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
                    System.IO.File.AppendAllText(logFilePath, logMessage);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL: Failed to write to log file: {ex.Message}");
            }
        }
    }

    public partial class ParentalControlForm : Form
    {
        private System.Windows.Forms.Timer? _timer;
        private KioskForm? _kioskFormInstance;
        private ITelegramBotClient? _botClient;
        private CancellationTokenSource? _cts;

        private bool _isLockdownActive = false;
        private bool _manualOverride = false;

        private string _mode = "live";
        private TimeSpan _startTime = new TimeSpan(22, 0, 0);
        private TimeSpan _endTime = new TimeSpan(7, 0, 0);
        private string _apiToken = "";

        private const string GuardianProcessName = "GuardianApp";
        private const string GuardianExeName = "GuardianApp.exe";
        private const string TaskName = "ParentalControlAppStartup";


        public ParentalControlForm()
        {
            InitializeComponent();
            CheckForPendingUpdate();
            SetupApplication();
        }

        private void CheckForPendingUpdate()
        {
            try
            {
                string newExePath = Application.ExecutablePath + ".new";
                if (System.IO.File.Exists(newExePath))
                {
                    Logger.Log("Found pending update file. Applying update...");
                    string oldExePath = Application.ExecutablePath + ".old";
                    
                    if(System.IO.File.Exists(oldExePath)) System.IO.File.Delete(oldExePath);
                    System.IO.File.Move(Application.ExecutablePath, oldExePath);
                    
                    System.IO.File.Move(newExePath, Application.ExecutablePath);
                    
                    Logger.Log("Update applied. Restarting application...");
                    Application.Restart();
                    Environment.Exit(0);
                }
            }
            catch(Exception ex)
            {
                Logger.Log($"Failed to apply pending update: {ex.ToString()}");
            }
        }

        private void SetupApplication()
        {
            try
            {
                this.WindowState = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
                this.Visible = false;

                MakeAppFolderHidden();
                LaunchGuardian();

                ReadSettings();
                InitializeBot();
                
                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = 15000;
                _timer.Tick += Timer_Tick;
                _timer.Start();

                Timer_Tick(null, EventArgs.Empty);
            }
            catch(Exception ex)
            {
                Logger.Log($"FATAL error during SetupApplication: {ex.ToString()}");
            }
        }

        private void MakeAppFolderHidden()
        {
             try
            {
                string flagFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".hidden_flag");
                if (!System.IO.File.Exists(flagFile))
                {
                    DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                    dir.Attributes |= FileAttributes.Hidden;
                    System.IO.File.Create(flagFile).Close();
                    Logger.Log("Application folder has been hidden.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not hide application folder: {ex.Message}");
            }
        }

        private void LaunchGuardian()
        {
            try
            {
                if (Process.GetProcessesByName(GuardianProcessName).Length == 0)
                {
                    string guardianPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GuardianExeName);
                    if (System.IO.File.Exists(guardianPath))
                    {
                        // Pass the application folder path as a command line argument
                        string arguments = $"\"{AppDomain.CurrentDomain.BaseDirectory}\"";
                        
                        Process.Start(new ProcessStartInfo(guardianPath, arguments) 
                        { 
                            UseShellExecute = true, 
                            Verb = "runas"  // Run with admin rights
                        });
                        Logger.Log("Guardian process was not running. Started it.");
                    }
                    else
                    {
                        Logger.Log($"CRITICAL: {GuardianExeName} not found.");
                    }
                }
            }
            catch(Exception ex)
            {
                 Logger.Log($"Failed to launch Guardian: {ex.ToString()}");
            }
        }
        
        #region Settings and File Management
        
        private string GetConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "links.txt");
        }

        private void ReadSettings()
        {
            try
            {
                string filePath = GetConfigPath();
                if (!System.IO.File.Exists(filePath))
                {
                    Logger.Log("Settings file links.txt not found.");
                    return;
                }

                var lines = System.IO.File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    if (line.StartsWith("mode=")) _mode = line.Substring(5).Trim();
                    if (line.StartsWith("startTime=")) TimeSpan.TryParse(line.Substring(10).Trim(), out _startTime);
                    if (line.StartsWith("endTime=")) TimeSpan.TryParse(line.Substring(8).Trim(), out _endTime);
                    if (line.StartsWith("API_TOKEN=")) _apiToken = line.Substring(10).Trim();
                }
            }
            catch(Exception ex)
            {
                Logger.Log($"Error in ReadSettings: {ex.ToString()}");
            }
        }
        
        private void UpdateSettingInFile(string key, string value)
        {
            try
            {
                string filePath = GetConfigPath();
                var lines = new List<string>(System.IO.File.Exists(filePath) ? System.IO.File.ReadAllLines(filePath) : new string[0]);
                int index = lines.FindIndex(line => line.StartsWith(key + "="));

                if (index != -1)
                {
                    lines[index] = $"{key}={value}";
                }
                else
                {
                    lines.Insert(0, $"{key}={value}");
                }
                System.IO.File.WriteAllLines(filePath, lines);
                ReadSettings();
            }
            catch(Exception ex)
            {
                Logger.Log($"Error in UpdateSettingInFile: {ex.ToString()}");
            }
        }

        private void AddLinkToFile(string url)
        {
            try
            {
                string filePath = GetConfigPath();
                System.IO.File.AppendAllText(filePath, Environment.NewLine + url);
            }
            catch(Exception ex)
            {
                Logger.Log($"Error in AddLinkToFile: {ex.ToString()}");
            }
        }

        private void RemoveLinkFromFile(int index)
        {
            try
            {
                string filePath = GetConfigPath();
                if (!System.IO.File.Exists(filePath)) return;

                var lines = new List<string>(System.IO.File.ReadAllLines(filePath));
                var linkLines = lines.Where(l => l.Contains("youtube.com")).ToList();
                
                if(index > 0 && index <= linkLines.Count)
                {
                    string lineToRemove = linkLines[index - 1];
                    lines.Remove(lineToRemove);
                    System.IO.File.WriteAllLines(filePath, lines);
                }
            }
            catch(Exception ex)
            {
                Logger.Log($"Error in RemoveLinkFromFile: {ex.ToString()}");
            }
        }

        #endregion

        #region Telegram Bot Logic

        private async void InitializeBot()
        {
            if (string.IsNullOrEmpty(_apiToken))
            {
                Logger.Log("API_TOKEN is not set. Telegram bot will not be initialized.");
                return;
            }

            try
            {
                _botClient = new TelegramBotClient(_apiToken);
                _cts = new CancellationTokenSource();
                
                Logger.Log("Attempting to delete any existing webhook...");
                await _botClient.DeleteWebhookAsync(cancellationToken: _cts.Token);
                Logger.Log("Webhook cleared successfully.");

                _botClient.StartReceiving(
                    updateHandler: HandleUpdateAsync,
                    pollingErrorHandler: HandlePollingErrorAsync,
                    cancellationToken: _cts.Token
                );

                var me = await _botClient.GetMeAsync();
                Logger.Log($"Telegram bot initialized successfully: {me.Username}");
            }
            catch(Exception ex)
            {
                Logger.Log($"Failed to initialize Telegram bot: {ex.ToString()}");
            }
        }

        private async System.Threading.Tasks.Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message) return;
            Logger.Log($"Received a message of type {message.Type} from chat {message.Chat.Id}.");
            
            try
            {
                if (message.Type == MessageType.Text && message.Text != null)
                {
                    await HandleTextMessage(botClient, message);
                }
                else if (message.Type == MessageType.Document)
                {
                    await HandleFileMessage(botClient, message);
                }
            }
            catch(Exception ex)
            {
                Logger.Log($"Error in HandleUpdateAsync: {ex.ToString()}");
                 try
                {
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Произошла внутренняя ошибка при обработке вашего запроса.",
                        cancellationToken: cancellationToken);
                }
                catch { }
            }
        }
        
        private async System.Threading.Tasks.Task HandleTextMessage(ITelegramBotClient botClient, Telegram.Bot.Types.Message message)
        {
            var messageText = message.Text ?? "";
            var commandParts = messageText.Split(' ');
            var command = commandParts[0].ToLower();
            var chatId = message.Chat.Id;

            switch (command)
            {
                case "/kill_all":
                    await botClient.SendTextMessageAsync(chatId, "EMERGENCY SHUTDOWN. Stopping all processes...");
                    Logger.Log("EMERGENCY SHUTDOWN INITIATED VIA TELEGRAM.");
                    
                    try 
                    {
                        using (var ts = new TaskService())
                        {
                            ts.RootFolder.DeleteTask(TaskName, false);
                        }
                    } catch (Exception ex) { Logger.Log($"Could not delete task: {ex.Message}"); }

                    try 
                    {
                       foreach (var p in Process.GetProcessesByName(GuardianProcessName)) p.Kill();
                    } catch (Exception ex) { Logger.Log($"Could not kill guardian: {ex.Message}"); }

                    Environment.Exit(0);
                    break;
                
                 case "/start_lock":
                    this.Invoke(new MethodInvoker(StartLockdown));
                    _manualOverride = true;
                    await botClient.SendTextMessageAsync(chatId, "Блокировка запущена принудительно.");
                    break;
                case "/stop_lock":
                    this.Invoke(new MethodInvoker(StopLockdown));
                    _manualOverride = false;
                    await botClient.SendTextMessageAsync(chatId, "Блокировка остановлена.");
                    break;
                case "/status":
                    string status = $"Статус: {( _isLockdownActive ? "АКТИВЕН" : "НЕ АКТИВЕН" )}\n" +
                                    $"Режим: {_mode}\n" +
                                    $"Время: с {_startTime:hh\\:mm} до {_endTime:hh\\:mm}\n" +
                                    "Ссылки:\n";
                    var lines = System.IO.File.ReadAllLines(GetConfigPath());
                    int i = 1;
                    foreach(var line in lines.Where(l => l.Contains("youtube.com")))
                    {
                        status += $"{i++}. {line}\n";
                    }
                    await botClient.SendTextMessageAsync(chatId, status);
                    break;
                case "/addlink":
                    if(commandParts.Length > 1 && commandParts[1].Contains("youtube.com"))
                    {
                        AddLinkToFile(commandParts[1]);
                        await botClient.SendTextMessageAsync(chatId, "Ссылка добавлена.");
                    } else {
                        await botClient.SendTextMessageAsync(chatId, "Неверный формат. Используйте: /addlink <URL>");
                    }
                    break;
                case "/removelink":
                     if(commandParts.Length > 1 && int.TryParse(commandParts[1], out int index))
                    {
                        RemoveLinkFromFile(index);
                        await botClient.SendTextMessageAsync(chatId, "Ссылка удалена.");
                    } else {
                        await botClient.SendTextMessageAsync(chatId, "Неверный формат. Используйте: /removelink <номер>");
                    }
                    break;
                case "/settime":
                    if(commandParts.Length > 2 && TimeSpan.TryParse(commandParts[1], out var newStart) && TimeSpan.TryParse(commandParts[2], out var newEnd))
                    {
                         UpdateSettingInFile("startTime", newStart.ToString("hh\\:mm"));
                         UpdateSettingInFile("endTime", newEnd.ToString("hh\\:mm"));
                         await botClient.SendTextMessageAsync(chatId, $"Новое время установлено: с {newStart:hh\\:mm} до {newEnd:hh\\:mm}.");
                    } else {
                         await botClient.SendTextMessageAsync(chatId, "Неверный формат. Используйте: /settime HH:mm HH:mm");
                    }
                    break;
                
                case "/getlog":
                    string logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error_log.txt");
                    if (System.IO.File.Exists(logFilePath))
                    {
                        string logContent = System.IO.File.ReadAllText(logFilePath);
                        if(string.IsNullOrWhiteSpace(logContent))
                        {
                             await botClient.SendTextMessageAsync(chatId, "Файл лога пуст.");
                        }
                        else 
                        {
                            if (logContent.Length > 4000)
                            {
                                logContent = "...\n" + logContent.Substring(logContent.Length - 4000);
                            }
                            await botClient.SendTextMessageAsync(chatId, logContent);
                        }
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(chatId, "Файл лога не найден.");
                    }
                    break;
                case "/getguardianlog":
                    string guardianLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "guardian_log.txt");
                    if (System.IO.File.Exists(guardianLogPath))
                    {
                        string logContent = System.IO.File.ReadAllText(guardianLogPath);
                         if(string.IsNullOrWhiteSpace(logContent))
                        {
                             await botClient.SendTextMessageAsync(chatId, "Файл лога сторожа пуст.");
                        }
                        else
                        {
                            if (logContent.Length > 4000)
                            {
                                logContent = "...\n" + logContent.Substring(logContent.Length - 4000);
                            }
                            await botClient.SendTextMessageAsync(chatId, logContent);
                        }
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(chatId, "Файл лога сторожа не найден.");
                    }
                    break;

                case "/help":
                     string helpText = "Доступные команды:\n" +
                        "/start_lock - Запустить блокировку сейчас\n" +
                        "/stop_lock - Остановить блокировку сейчас\n" +
                        "/status - Показать текущий статус и настройки\n" +
                        "/addlink <URL> - Добавить ссылку\n" +
                        "/removelink <номер> - Удалить ссылку по номеру из /status\n" +
                        "/settime <старт HH:mm> <конец HH:mm> - Установить время\n" +
                        "/getlog - Получить лог основного приложения\n" +
                        "/getguardianlog - Получить лог сторожа\n" +
                        "/kill_all - ЭКСТРЕННАЯ ОСТАНОВКА\n\n" +
                        "Для обновления отправьте ZIP-архив с файлами приложения.";
                     await botClient.SendTextMessageAsync(chatId, helpText);
                    break;
                
                default:
                    await botClient.SendTextMessageAsync(chatId, "Неизвестная команда. Используйте /help для списка команд.");
                    break;
            }
        }
        
        private async System.Threading.Tasks.Task HandleFileMessage(ITelegramBotClient botClient, Telegram.Bot.Types.Message message)
        {
            if (message.Document?.FileName != null && message.Document.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Получен ZIP-архив обновления. Начинаю процесс...");
                    
                    var fileId = message.Document.FileId;
                    var fileInfo = await botClient.GetFileAsync(fileId);
                    var filePath = fileInfo.FilePath;
                    if(filePath == null) {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Не удалось получить путь к файлу обновления.");
                        Logger.Log("Failed to get file path for update from Telegram.");
                        return;
                    }

                    string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    string zipPath = Path.Combine(appDirectory, "update.zip");
                    string extractPath = Path.Combine(appDirectory, "update_temp");

                    await using (FileStream fileStream = System.IO.File.OpenWrite(zipPath))
                    {
                        await botClient.DownloadFileAsync(filePath, fileStream);
                    }
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Архив загружен. Распаковываю...");

                    if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                    ZipFile.ExtractToDirectory(zipPath, extractPath, true);
                    System.IO.File.Delete(zipPath);

                    string updateScriptPath = Path.Combine(appDirectory, "apply_update.bat");

                    string scriptContent = $@"
@echo off
echo Stopping processes...
taskkill /IM {GuardianExeName} /F
taskkill /IM {Path.GetFileName(Application.ExecutablePath)} /F
timeout /t 5 /nobreak > NUL
echo Copying new files...
xcopy ""{extractPath}"" ""{appDirectory}"" /E /Y /I
echo Cleaning up...
rmdir /s /q ""{extractPath}""
echo Starting new version...
start """" ""{Application.ExecutablePath}""
del ""%~f0""
";
                    System.IO.File.WriteAllText(updateScriptPath, scriptContent);
                    
                    Process.Start(new ProcessStartInfo(updateScriptPath) { UseShellExecute = true, Verb = "runas" });
                    
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Скрипт обновления запущен. Приложение будет перезапущено.");
                    Logger.Log("Update script launched. Application is exiting.");
                    Application.Exit();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error during ZIP update: {ex.ToString()}");
                    await botClient.SendTextMessageAsync(message.Chat.Id, $"Ошибка при обновлении из архива: {ex.Message}");
                }
            }
        }

        private System.Threading.Tasks.Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };
            
            Logger.Log(errorMessage);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        #endregion

        #region Lockdown Logic

        private void Timer_Tick(object? sender, EventArgs e)
        {
            try
            {
                // **FIX**: Periodically check for the guardian process
                LaunchGuardian();

                if (_isLockdownActive)
                {
                    EnforceProcessLockdown();
                }

                if (_manualOverride) return;
                if (_mode == "debug") {
                     if(!_isLockdownActive) StartLockdown();
                     return;
                }

                var now = DateTime.Now.TimeOfDay;
                bool shouldBeActive = false;
                
                if (_startTime > _endTime) 
                {
                    shouldBeActive = now >= _startTime || now < _endTime;
                }
                else 
                {
                    shouldBeActive = now >= _startTime && now < _endTime;
                }
                
                if (shouldBeActive && !_isLockdownActive)
                {
                    StartLockdown();
                }
                else if (!shouldBeActive && _isLockdownActive)
                {
                    StopLockdown();
                }
            }
            catch(Exception ex)
            {
                Logger.Log($"Error in Timer_Tick: {ex.ToString()}");
            }
        }
        
        private void EnforceProcessLockdown()
        {
            var whiteList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "explorer", "svchost", "lsass", "winlogon", "csrss", "smss", 
                "wininit", "services", "dwm", "taskhostw", "fontdrvhost",
                GuardianProcessName,
                Path.GetFileNameWithoutExtension(Application.ExecutablePath)
            };

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero && !whiteList.Contains(process.ProcessName))
                    {
                         if (_mode == "test")
                        {
                            Logger.Log($"[TEST MODE] Would have terminated process: {process.ProcessName} (ID: {process.Id})");
                        }
                        else
                        {
                            process.Kill();
                            Logger.Log($"Terminated unauthorized process: {process.ProcessName} (ID: {process.Id})");
                        }
                    }
                }
                catch {}
            }
        }
        
        private void StartLockdown()
        {
            if (_isLockdownActive) return;
            
            _isLockdownActive = true;
            Logger.Log("Starting lockdown.");
            
            EnforceProcessLockdown();
            
            _kioskFormInstance = new KioskForm(_mode);
            _kioskFormInstance.FormClosed += (s, args) => {
                _isLockdownActive = false;
                _kioskFormInstance = null;
                Logger.Log("Kiosk form closed, lockdown ended.");
            };
            _kioskFormInstance.Show();
        }
        
        private void StopLockdown()
        {
            if (_kioskFormInstance != null && !_kioskFormInstance.IsDisposed)
            {
                Logger.Log("Stopping lockdown via StopLockdown method.");
                _kioskFormInstance.Invoke(new MethodInvoker(() => {
                    _kioskFormInstance.UnlockAndClose();
                }));
            }
            else
            {
                _isLockdownActive = false;
                Logger.Log("StopLockdown called, but no active kiosk form instance found. Resetting flag.");
            }
        }
        
        #endregion

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.ClientSize = new System.Drawing.Size(284, 261);
            this.Name = "ParentalControlForm";
            this.Text = "Parental Control Background";
            this.Load += (sender, e) => {
                BeginInvoke(new MethodInvoker(delegate { Hide(); }));
            };
            this.ResumeLayout(false);
        }
    }
}
