using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

const string ServiceName = "Starrboard";
const string LegacyServiceName = "ArrDiscordBot";
const string ServiceAccount = @"NT AUTHORITY\LocalService";
const string ConsoleUrl = "http://127.0.0.1:8686";
const string ProtocolName = "starrboard";
var launchedHelper = false;

try
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("Starrboard installer supports Windows only.");
    }

    var options = InstallerOptions.Parse(args);
    if (!string.IsNullOrWhiteSpace(options.LaunchAction))
    {
        SetupUi.HideLauncherConsole();
    }
    if (!IsAdministrator())
    {
        RelaunchElevated(args);
        return;
    }

    if (!string.IsNullOrWhiteSpace(options.LaunchAction))
    {
        LaunchHelper(options.LaunchAction);
        launchedHelper = true;
        return;
    }

    if (!options.InstallRoot.Equals(InstallerDefaults.DefaultInstallRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new NotSupportedException($"Custom install roots are not supported by this release. Use {InstallerDefaults.DefaultInstallRoot}.");
    }

    var packageRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var publishSource = Path.Combine(packageRoot, "publish");
    var templateSource = Path.Combine(packageRoot, "appsettings.template.json");
    var installRoot = options.InstallRoot;
    var publishTarget = Path.Combine(installRoot, "bin");
    var dataRoot = Path.Combine(installRoot, "data");
    var logsRoot = Path.Combine(installRoot, "logs");
    var backupsRoot = Path.Combine(installRoot, "backups");
    var runtimeConfig = Path.Combine(dataRoot, "appsettings.runtime.json");
    var existingInstall = File.Exists(runtimeConfig);
    var exePath = Path.Combine(publishTarget, "Starrboard.exe");

    Console.WriteLine("Starrboard installer");
    Console.WriteLine($"Package: {packageRoot}");
    Console.WriteLine($"Install root: {installRoot}");
    Console.WriteLine();

    RequireFile(Path.Combine(publishSource, "Starrboard.exe"), "Package publish folder is missing Starrboard.exe.");
    RequireFile(templateSource, "Package template config is missing.");

    Directory.CreateDirectory(installRoot);
    Directory.CreateDirectory(publishTarget);
    Directory.CreateDirectory(dataRoot);
    Directory.CreateDirectory(logsRoot);
    Directory.CreateDirectory(backupsRoot);

    StopServiceIfPresent(ServiceName);
    StopServiceIfPresent(LegacyServiceName);
    DeleteServiceIfPresent(LegacyServiceName);

    var migrated = MigrateLegacyInstall(dataRoot, logsRoot);

    Console.WriteLine("Applying filesystem permissions...");
    SecureInstallRoot(installRoot, publishTarget, dataRoot, logsRoot, backupsRoot, resetChildren: !existingInstall && !migrated);

    Console.WriteLine("Copying published service files...");
    MirrorDirectory(publishSource, publishTarget);

    CopyIfExists(packageRoot, installRoot, "Set-StarrboardToken.ps1");
    CopyIfExists(packageRoot, installRoot, "Input-StarrboardToken.ps1");
    CopyIfExists(packageRoot, installRoot, "Start-StarrboardService.ps1");
    CopyIfExists(packageRoot, installRoot, "Ensure-StarrboardService.ps1");
    CopyIfExists(packageRoot, installRoot, "Open-StarrboardConsole.cmd");
    CopyIfExists(packageRoot, installRoot, "Open-StarrboardConsole.ps1");
    CopyIfExists(packageRoot, installRoot, "Uninstall-Starrboard.ps1");
    CopyIfExists(packageRoot, installRoot, "Uninstall-Starrboard.cmd");
    CopyIfExists(packageRoot, installRoot, "Test-Starrboard.ps1");
    CopyIfExists(packageRoot, installRoot, "README.md");
    CopySelfIfNeeded(installRoot);
    WriteLauncherFiles(installRoot);

    File.Copy(templateSource, Path.Combine(installRoot, "appsettings.template.json"), overwrite: true);
    File.Copy(templateSource, Path.Combine(publishTarget, "appsettings.json"), overwrite: true);
    File.Copy(templateSource, Path.Combine(publishTarget, "appsettings.template.json"), overwrite: true);
    if (!File.Exists(runtimeConfig))
    {
        File.Copy(templateSource, runtimeConfig, overwrite: false);
    }


    Console.WriteLine("Installing Windows service...");
    var imagePath = Quote(exePath);
    if (ServiceExists(ServiceName))
    {
        Run("sc.exe", $"config {ServiceName} binPath= {Quote(imagePath)} start= auto obj= {Quote(ServiceAccount)}");
    }
    else
    {
        Run("sc.exe", $"create {ServiceName} binPath= {Quote(imagePath)} start= auto obj= {Quote(ServiceAccount)} DisplayName= {Quote("Starrboard")}");
    }

    Run("sc.exe", $"description {ServiceName} {Quote("Private Discord request board for Radarr and Sonarr.")}");
    Run("sc.exe", $"config {ServiceName} start= delayed-auto");
    Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000");
    Run("sc.exe", $"failureflag {ServiceName} 1");
    var bootRecoveryScript = Path.Combine(installRoot, "Ensure-StarrboardService.ps1");
    if (File.Exists(bootRecoveryScript))
    {
        var bootCommand = $"powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File {Quote(bootRecoveryScript)}";
        Run("schtasks.exe", $"/Create /TN {Quote("Starrboard - Boot Recovery")} /TR {Quote(bootCommand)} /SC ONSTART /DELAY 0003:00 /RU SYSTEM /RL HIGHEST /F");
    }
    RegisterProtocol(Path.Combine(installRoot, "Install-Starrboard.exe"));
    WriteDesktopShortcut(installRoot);

    if (!options.NoStart)
    {
        Console.WriteLine("Starting Starrboard service...");
        Run("sc.exe", $"start {ServiceName}", allowFailure: true);
        WaitForServiceRunning(TimeSpan.FromSeconds(60));
        if (!await WaitForConsoleReadyAsync(TimeSpan.FromSeconds(120)))
        {
            Console.WriteLine("Warning: Starrboard service is running, but the console API did not answer yet.");
            Console.WriteLine($"Open {ConsoleUrl} manually in a minute, or check {Path.Combine(logsRoot, "Starrboard.log")} if it stays unavailable.");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Starrboard installed.");
    if (migrated)
    {
        Console.WriteLine($"Settings and request history were copied from {InstallerDefaults.LegacyInstallRoot}.");
        Console.WriteLine("That folder was left in place. Delete it after you confirm Starrboard works.");
    }
    Console.WriteLine($"Console: {ConsoleUrl}");
    Console.WriteLine("Open the console manually, or run this installer with --open to open it automatically.");

    if (!options.NoOpen)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ConsoleUrl,
            UseShellExecute = true
        });
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Install failed:");
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
finally
{
    if (!launchedHelper && Environment.UserInteractive && !Console.IsInputRedirected)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to exit.");
        Console.ReadLine();
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void RelaunchElevated(string[] args)
{
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate installer executable.");
    var startInfo = new ProcessStartInfo
    {
        FileName = exe,
        Arguments = string.Join(" ", args.Select(Quote)),
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = AppContext.BaseDirectory
    };
    Process.Start(startInfo);
}

static void RequireFile(string path, string message)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException(message, path);
    }
}

// Every SYSTEM scheduled task Starrboard creates runs files from under the install root,
// so only SYSTEM and Administrators may write there. Secrets in data and backups are
// readable only by SYSTEM, Administrators, and the service account.
static void SecureInstallRoot(string root, string bin, string data, string logs, string backups, bool resetChildren)
{
    const string SystemSid = "*S-1-5-18";
    const string AdministratorsSid = "*S-1-5-32-544";
    const string UsersSid = "*S-1-5-32-545";
    const string LocalServiceSid = "*S-1-5-19";

    Run("icacls", $"{Quote(root)} /setowner {AdministratorsSid} /T /C /Q", allowFailure: true);
    Run("icacls", $"{Quote(root)} /inheritance:r /grant:r {SystemSid}:(OI)(CI)F {AdministratorsSid}:(OI)(CI)F {UsersSid}:(OI)(CI)RX /Q");
    if (resetChildren)
    {
        // Fresh install into a folder that already existed: drop any explicit permissions
        // someone else may have placed on it before Starrboard was installed.
        Run("icacls", $"{Quote(Path.Combine(root, "*"))} /reset /T /C /Q", allowFailure: true);
    }

    Run("icacls", $"{Quote(bin)} /grant {LocalServiceSid}:(OI)(CI)RX /Q");
    foreach (var secretDirectory in new[] { data, backups })
    {
        Run("icacls", $"{Quote(secretDirectory)} /inheritance:r /grant:r {SystemSid}:(OI)(CI)F {AdministratorsSid}:(OI)(CI)F /Q");
    }

    Run("icacls", $"{Quote(data)} /grant {LocalServiceSid}:(OI)(CI)M /Q");
    Run("icacls", $"{Quote(logs)} /grant {LocalServiceSid}:(OI)(CI)M /Q");
}

// Releases before 0.2.0 installed to C:\Util\arr-discord-bot. Copy that install's settings,
// request database, and dependency manifest so an upgrade keeps working.
static bool MigrateLegacyInstall(string dataRoot, string logsRoot)
{
    var legacyData = Path.Combine(InstallerDefaults.LegacyInstallRoot, "data");
    var legacyConfig = Path.Combine(legacyData, "appsettings.runtime.json");
    var runtimeConfig = Path.Combine(dataRoot, "appsettings.runtime.json");
    if (!File.Exists(legacyConfig) || File.Exists(runtimeConfig))
    {
        return false;
    }

    Console.WriteLine($"Migrating settings and request history from {legacyData}...");
    Run("robocopy", $"{Quote(legacyData)} {Quote(dataRoot)} /E /R:2 /W:2 /NP /NFL /NDL", successExitCode: 7);

    var root = JsonNode.Parse(File.ReadAllText(runtimeConfig))?.AsObject();
    if (root?["Bot"] is JsonObject bot)
    {
        if (PathEquals(bot["DatabasePath"], InstallerDefaults.LegacyDatabasePath))
        {
            bot["DatabasePath"] = Path.Combine(dataRoot, "requests.db");
        }

        if (bot["Logs"] is JsonObject logs && PathEquals(logs["Path"], InstallerDefaults.LegacyLogPath))
        {
            logs["Path"] = Path.Combine(logsRoot, "Starrboard.log");
        }

        File.WriteAllText(runtimeConfig, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    return true;
}

static bool PathEquals(JsonNode? node, string expected) =>
    node is JsonValue value &&
    value.TryGetValue<string>(out var text) &&
    string.Equals(text.TrimEnd('\\'), expected, StringComparison.OrdinalIgnoreCase);

static void MirrorDirectory(string source, string destination)
{
    Run("robocopy", $"{Quote(source)} {Quote(destination)} /MIR", successExitCode: 7);
}

static void CopyIfExists(string sourceRoot, string targetRoot, string fileName)
{
    var source = Path.Combine(sourceRoot, fileName);
    if (File.Exists(source))
    {
        File.Copy(source, Path.Combine(targetRoot, fileName), overwrite: true);
    }
}

static void CopySelfIfNeeded(string installRoot)
{
    var source = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
    {
        return;
    }

    var destination = Path.Combine(installRoot, "Install-Starrboard.exe");
    if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    File.Copy(source, destination, overwrite: true);
}

static void WriteLauncherFiles(string installRoot)
{
    File.WriteAllText(
        Path.Combine(installRoot, "Run-StarrboardDependencySetup.cmd"),
        """
@echo off
start "" starrboard://bootstrap
""");

    File.WriteAllText(
        Path.Combine(installRoot, "Run-StarrboardServiceAccountSetup.cmd"),
        """
@echo off
start "" starrboard://service-account
""");
}

static void RegisterProtocol(string launcherPath)
{
    var command = $"{Quote(launcherPath)} --launch \"%1\"";
    Run("reg.exe", $"add HKLM\\SOFTWARE\\Classes\\{ProtocolName} /ve /d {Quote("URL:Starrboard Launcher")} /f");
    Run("reg.exe", $"add HKLM\\SOFTWARE\\Classes\\{ProtocolName} /v \"URL Protocol\" /d \"\" /f");
    Run("reg.exe", $"add HKLM\\SOFTWARE\\Classes\\{ProtocolName}\\DefaultIcon /ve /d {Quote(launcherPath + ",0")} /f");
    Run("reg.exe", $"add HKLM\\SOFTWARE\\Classes\\{ProtocolName}\\shell\\open\\command /ve /d {Quote(command)} /f");
}

static void WriteDesktopShortcut(string installRoot)
{
    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    if (string.IsNullOrWhiteSpace(desktop))
    {
        desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    if (string.IsNullOrWhiteSpace(desktop))
    {
        Console.WriteLine("Warning: could not determine a desktop path for the Starrboard shortcut.");
        return;
    }

    Directory.CreateDirectory(desktop);
    var shortcutPath = Path.Combine(desktop, "Starrboard Console.url");
    var iconPath = Path.Combine(installRoot, "bin", "admin", "assets", "starrboard.ico");
    if (!File.Exists(iconPath)) iconPath = Path.Combine(installRoot, "Install-Starrboard.exe");
    File.WriteAllText(
        shortcutPath,
        $"""
[InternetShortcut]
URL={ConsoleUrl}
IconFile={iconPath}
IconIndex=0
""");
    Console.WriteLine($"Desktop shortcut: {shortcutPath}");
}

static void LaunchHelper(string action)
{
    var normalized = action.Trim().ToLowerInvariant();
    if (normalized.Contains("restart", StringComparison.Ordinal))
    {
        LaunchRestartHelper();
        return;
    }

    if (normalized.Contains("service-account", StringComparison.Ordinal))
    {
        SetupUi.ShowServiceAccount("http://127.0.0.1:8686/api/services/account-script");
        return;
    }

    if (normalized.Contains("bootstrap", StringComparison.Ordinal) || normalized.Contains("dependencies", StringComparison.Ordinal))
    {
        SetupUi.ShowDependencyInstaller("http://127.0.0.1:8686/api/bootstrap/script");
        return;
    }

    throw new InvalidOperationException($"Unknown Starrboard launcher action: {action}");
}

static void LaunchRestartHelper()
{
    var launcherPath = Path.Combine(Path.GetTempPath(), "StarrboardRestart-" + Guid.NewGuid().ToString("N") + ".ps1");
    File.WriteAllText(
        launcherPath,
        """
$ErrorActionPreference = 'Stop'
Write-Host 'Restarting Starrboard service...'
Restart-Service -Name Starrboard -Force
Start-Sleep -Seconds 3
Get-Service -Name Starrboard | Select-Object Name,Status,StartType | Format-List
Write-Host ''
Read-Host 'Press Enter to close'
""");

    Process.Start(new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(launcherPath)}",
        UseShellExecute = true
    });
}

static void WaitForServiceRunning(TimeSpan timeout)
{
    var deadline = DateTimeOffset.Now.Add(timeout);
    while (DateTimeOffset.Now < deadline)
    {
        var status = GetServiceStatus(ServiceName);
        if (status.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(2));
    }

    throw new InvalidOperationException($"Starrboard service did not report RUNNING within {timeout.TotalSeconds:0} seconds. Last status: {GetServiceStatus(ServiceName)}");
}

static string GetServiceStatus(string name)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "sc.exe",
        Arguments = $"query {name}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not query service status.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return string.IsNullOrWhiteSpace(output) ? error.Trim() : output.Trim();
}

static async Task<bool> WaitForConsoleReadyAsync(TimeSpan timeout)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    var deadline = DateTimeOffset.Now.Add(timeout);
    Exception? lastError = null;
    while (DateTimeOffset.Now < deadline)
    {
        try
        {
            using var response = await http.GetAsync(ConsoleUrl.TrimEnd('/') + "/api/ready");
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            lastError = new InvalidOperationException($"Console returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    Console.WriteLine($"Console readiness check timed out. Last error: {lastError?.Message}");
    return false;
}

static bool ServiceExists(string name)
{
    var result = Run("sc.exe", $"query {name}", allowFailure: true);
    return result == 0;
}

static void StopServiceIfPresent(string name)
{
    if (!ServiceExists(name))
    {
        return;
    }

    Run("sc.exe", $"stop {name}", allowFailure: true);
    Thread.Sleep(TimeSpan.FromSeconds(2));
}

static void DeleteServiceIfPresent(string name)
{
    if (ServiceExists(name))
    {
        Run("sc.exe", $"delete {name}", allowFailure: true);
        Thread.Sleep(TimeSpan.FromSeconds(2));
    }
}

static int Run(string fileName, string arguments, int successExitCode = 0, bool allowFailure = false)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (!string.IsNullOrWhiteSpace(output))
    {
        Console.WriteLine(output.Trim());
    }
    if (!string.IsNullOrWhiteSpace(error))
    {
        Console.Error.WriteLine(error.Trim());
    }

    if (process.ExitCode > successExitCode && !allowFailure)
    {
        throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
    }

    return process.ExitCode;
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

public sealed record InstallerOptions(string InstallRoot, bool NoStart, bool NoOpen, string LaunchAction)
{
    public static InstallerOptions Parse(string[] args)
    {
        var installRoot = InstallerDefaults.DefaultInstallRoot;
        var noStart = false;
        var noOpen = true;
        var launchAction = "";

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--launch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                launchAction = args[++i];
            }
            else if (arg.StartsWith("starrboard://", StringComparison.OrdinalIgnoreCase))
            {
                launchAction = arg;
            }
            else if (arg.Equals("--install-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                installRoot = args[++i];
            }
            else if (arg.Equals("--no-start", StringComparison.OrdinalIgnoreCase))
            {
                noStart = true;
            }
            else if (arg.Equals("--no-open", StringComparison.OrdinalIgnoreCase))
            {
                noOpen = true;
            }
            else if (arg.Equals("--open", StringComparison.OrdinalIgnoreCase))
            {
                noOpen = false;
            }
        }

        return new InstallerOptions(installRoot, noStart, noOpen, launchAction);
    }
}

public static class InstallerDefaults
{
    public static readonly string DefaultInstallRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Starrboard");

    public const string LegacyInstallRoot = @"C:\Util\arr-discord-bot";
    public const string LegacyDatabasePath = @"C:\Util\arr-discord-bot\data\requests.db";
    public const string LegacyLogPath = @"C:\Util\logs\Starrboard.log";
}
