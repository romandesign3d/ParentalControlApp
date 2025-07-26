# ParentalControlApp
This is a Windows application designed to enforce a "sleep mode" on a child's computer.
# **Reliable Parental Control**

This is a Windows application designed to enforce a "sleep mode" on a child's computer. At a set time, it locks down the system, providing access only to pre-approved content (e.g., fairy tales on YouTube) and features multi-level protection against attempts to bypass it.

## **🚀 Key Features**

* **Kiosk Mode:** At the scheduled hour, all other applications are closed, and the system enters a full-screen mode that cannot be minimized or closed by standard means.  
* **Content Whitelist:** Access is granted only to a list of YouTube videos defined by the parents. All other content, including search and related videos, is blocked.  
* **Remote Management via Telegram:**  
  * Start and stop the lockdown manually.  
  * Change the time intervals.  
  * Add and remove allowed links.  
  * Retrieve logs for diagnostics.  
  * **Automatic updates** by sending a ZIP archive of the application to the bot.  
* **Multi-level Protection:**  
  * **Hotkey Blocking:** Alt+F4, Ctrl+Alt+Del, and other system shortcuts are disabled during lockdown mode.  
  * **Guardian Process:** A helper application, GuardianApp.exe, constantly monitors the main process and immediately restarts it if it's terminated via Task Manager.  
  * **Mutual Monitoring:** The main application also monitors the Guardian and restarts it if it gets closed.  
  * **Autostart Control:** The Guardian checks for the existence of the scheduled task in Windows Task Scheduler and automatically recreates it if deleted.  
  * **Constant Process Monitoring:** During the active phase, the program constantly checks for and terminates any unauthorized processes.  
* **Stealth Operation:** The application runs in the background, has no tray icon, and its folder is hidden by default.  
* **Flexible Configuration:** All parameters (time, mode, links, bot token) are configured in a simple text file, links.txt.

## **🛠️ Tech Stack**

* **C\# / .NET 8**  
* **Windows Forms** for creating windowed applications.  
* **WebView2** for embedding a modern web browser.  
* **Telegram.Bot** for integration with the Telegram API.  
* **TaskScheduler Library** for programmatically managing the Windows Task Scheduler.

## **⚙️ Installation and Setup**

1. **Build the Projects:**  
   * Clone the repository.  
   * Ensure you have the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed.  
   * Build both projects in Release mode using the following terminal commands:  
     \# First, the Guardian  
     cd GuardianApp  
     dotnet publish \-c Release

     \# Then, the main application  
     cd ../ParentalControlApp  
     dotnet publish \-c Release

2. **Prepare the Application Folder:**  
   * Create a new folder where the application will be installed.  
   * Copy **all contents** from the ParentalControlApp/bin/Release/net8.0-windows/publish/ folder into it.  
   * From the GuardianApp/bin/Release/net8.0/publish/ folder, copy the GuardianApp.exe file into the same folder.  
3. **Configure links.txt:**  
   * Create a links.txt file in the application folder.  
   * Fill it out according to the example:  
     \# mode=live (production) | test (no process killing) | debug (immediate start)  
     mode=live  
     startTime=22:00  
     endTime=07:00  
     API\_TOKEN=YOUR\_TELEGRAM\_BOT\_TOKEN

     https://www.youtube.com/watch?v=...  
     https://www.youtube.com/watch?v=...

4. **First Launch:**  
   * Run ParentalControlApp.exe **as an administrator**.  
   * On its first run, the application will automatically hide its folder and create a task in the Windows Task Scheduler to run with administrative privileges on system startup.

## **📲 Using the Telegram Bot**

Send the /help command to your bot to get a current list of commands. The main ones include:

* /start\_lock, /stop\_lock \- Manually control the lockdown.  
* /status \- Display current settings and the list of links.  
* /settime \<start\> \<end\> \- Set a new time interval.  
* /addlink \<URL\>, /removelink \<number\> \- Manage links.  
* /getlog, /getguardianlog \- Retrieve log files.  
* /kill\_all \- **Emergency shutdown** of both processes and deletion of the scheduled task.

To update, simply send a **ZIP archive** with the new application files to the bot.

## **📄 License**

This project is distributed under the MIT License. See the LICENSE file for more details.
