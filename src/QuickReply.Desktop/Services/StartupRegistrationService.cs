using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace QuickReply.Desktop.Services;

public readonly record struct StartupConfigurationResult(bool Success, string Message);

public readonly record struct StartupActivationResult(bool Relaunched, string Message, bool UserCancelled);

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "QuickReplyTool";
    private const string LegacyElevatedTaskName = "QuickReplyTool.AdminLaunch";
    private const string ElevatedTaskNamePrefix = "QuickReplyTool.AdminLaunch.";
    private const int TaskActionExec = 0;
    private const int TaskTriggerLogon = 9;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int FileNotFoundHResult = unchecked((int)0x80070002);

    public bool IsEnabled()
    {
        return HasRegistryRunEntry() || HasElevatedTask();
    }

    public void SetEnabled(bool enabled)
    {
        ApplyConfiguration(enabled, runAsAdministrator: false);
    }

    public StartupConfigurationResult ApplyConfiguration(bool launchAtStartup, bool runAsAdministrator)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new StartupConfigurationResult(false, "未找到程序路径，启动配置未生效。");
        }

        try
        {
            if (runAsAdministrator)
            {
                RegisterOrUpdateElevatedTask(executablePath, launchAtStartup);
                RemoveRegistryRunEntry();
            }
            else
            {
                RemoveElevatedTask();
                SetRegistryRunEntry(launchAtStartup, executablePath);
            }

            return new StartupConfigurationResult(true, BuildConfigurationMessage(launchAtStartup, runAsAdministrator));
        }
        catch (Exception ex)
        {
            return new StartupConfigurationResult(false, $"启动配置未完全应用：{SimplifyMessage(ex.Message)}");
        }
    }

    public StartupActivationResult EnsureConfiguredLaunch(bool runAsAdministrator)
    {
        if (!runAsAdministrator || IsCurrentProcessElevated())
        {
            return default;
        }

        try
        {
            if (HasElevatedTask())
            {
                StartElevatedTask();
                return new StartupActivationResult(true, string.Empty, false);
            }
        }
        catch
        {
        }

        return RelaunchElevated();
    }

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string BuildConfigurationMessage(bool launchAtStartup, bool runAsAdministrator)
    {
        if (runAsAdministrator && launchAtStartup)
        {
            return "已启用管理员权限启动，并会在登录后自动启动。";
        }

        if (runAsAdministrator)
        {
            return "已启用管理员权限启动。";
        }

        if (launchAtStartup)
        {
            return "已启用开机自动启动。";
        }

        return "已关闭自动启动。";
    }

    private static StartupActivationResult RelaunchElevated()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new StartupActivationResult(false, "未找到程序路径，无法切换到管理员权限启动。", false);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                Arguments = BuildCommandLineArguments()
            };

            Process.Start(startInfo);
            return new StartupActivationResult(true, string.Empty, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new StartupActivationResult(false, "已取消管理员启动，本次将继续以普通权限运行，部分功能可能受限。", true);
        }
        catch (Exception ex)
        {
            return new StartupActivationResult(false, $"切换到管理员权限启动失败：{SimplifyMessage(ex.Message)}", false);
        }
    }

    private static string BuildCommandLineArguments()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return "\"\"";
        }

        if (!arg.Contains('"') && !arg.Any(char.IsWhiteSpace))
        {
            return arg;
        }

        return $"\"{arg.Replace("\"", "\\\"")}\"";
    }

    private bool HasRegistryRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(RunValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    private void SetRegistryRunEntry(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (key is null)
        {
            throw new InvalidOperationException("无法访问开机启动注册表项。");
        }

        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(RunValueName, $"\"{executablePath}\"");
    }

    private void RemoveRegistryRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private bool HasElevatedTask()
    {
        try
        {
            var task = GetElevatedTask(requireExecutableMatch: true);
            return task is not null;
        }
        catch
        {
            return false;
        }
    }

    private void StartElevatedTask()
    {
        dynamic task = GetElevatedTask(requireExecutableMatch: true) ?? throw new InvalidOperationException("未找到管理员启动任务。");
        task.Run(Type.Missing);
        SingleInstanceService.RequestExistingInstanceActivation();
    }

    private void RegisterOrUpdateElevatedTask(string executablePath, bool launchAtStartup)
    {
        CleanupLegacyElevatedTask();

        dynamic service = ConnectTaskService();
        dynamic rootFolder = service.GetFolder("\\");
        dynamic taskDefinition = service.NewTask(0);

        taskDefinition.RegistrationInfo.Description = "QuickReplyTool 管理员权限启动任务";
        taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
        taskDefinition.Principal.RunLevel = TaskRunLevelHighest;
        taskDefinition.Settings.Enabled = true;
        taskDefinition.Settings.AllowDemandStart = true;
        taskDefinition.Settings.StartWhenAvailable = true;
        taskDefinition.Settings.Hidden = false;
        taskDefinition.Settings.DisallowStartIfOnBatteries = false;
        taskDefinition.Settings.StopIfGoingOnBatteries = false;

        dynamic action = taskDefinition.Actions.Create(TaskActionExec);
        action.Path = executablePath;
        action.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;

        if (launchAtStartup)
        {
            dynamic trigger = taskDefinition.Triggers.Create(TaskTriggerLogon);
            trigger.Enabled = true;
            trigger.UserId = WindowsIdentity.GetCurrent().Name;
        }

        rootFolder.RegisterTaskDefinition(
            GetElevatedTaskName(),
            taskDefinition,
            TaskCreateOrUpdate,
            Type.Missing,
            Type.Missing,
            TaskLogonInteractiveToken,
            null);
    }

    private void RemoveElevatedTask()
    {
        dynamic service = ConnectTaskService();
        dynamic rootFolder = service.GetFolder("\\");

        DeleteTask(rootFolder, GetElevatedTaskName());
        CleanupLegacyElevatedTask(rootFolder);
    }

    private static dynamic? GetElevatedTask(bool requireExecutableMatch)
    {
        dynamic service = ConnectTaskService();
        object rootFolder = service.GetFolder("\\");
        return TryGetManagedTask(rootFolder, GetElevatedTaskName(), requireExecutableMatch)
            ?? TryGetManagedTask(rootFolder, LegacyElevatedTaskName, requireExecutableMatch, CanManageLegacyTask);
    }

    private static bool IsTaskExecutableMatch(dynamic task)
    {
        var currentExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutablePath))
        {
            return false;
        }

        try
        {
            var actionCount = (int)task.Definition.Actions.Count;
            if (actionCount < 1)
            {
                return false;
            }

            dynamic action = task.Definition.Actions.Item(1);
            var taskExecutablePath = Convert.ToString(action.Path) ?? string.Empty;
            return string.Equals(taskExecutablePath.Trim(), currentExecutablePath.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetElevatedTaskName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            return ElevatedTaskNamePrefix + "UnknownUser";
        }

        return ElevatedTaskNamePrefix + sid;
    }

    private void CleanupLegacyElevatedTask()
    {
        dynamic service = ConnectTaskService();
        dynamic rootFolder = service.GetFolder("\\");
        CleanupLegacyElevatedTask(rootFolder);
    }

    private void CleanupLegacyElevatedTask(dynamic rootFolder)
    {
        object rootFolderObject = rootFolder;
        Func<dynamic, bool> legacyTaskGuard = CanManageLegacyTask;
        dynamic? task = TryGetManagedTask(
            rootFolderObject,
            LegacyElevatedTaskName,
            true,
            legacyTaskGuard);

        if (task is null)
        {
            return;
        }

        DeleteTask(rootFolder, LegacyElevatedTaskName);
    }

    private void DeleteTask(dynamic rootFolder, string taskName)
    {
        try
        {
            rootFolder.DeleteTask(taskName, 0);
        }
        catch (COMException ex) when (ex.HResult == FileNotFoundHResult)
        {
        }
    }

    private static dynamic? TryGetManagedTask(
        object rootFolderObject,
        string taskName,
        bool requireExecutableMatch,
        Func<dynamic, bool>? additionalGuard = null)
    {
        dynamic? task = TryGetTask(rootFolderObject, taskName);
        if (task is null)
        {
            return null;
        }

        if (additionalGuard is not null && !additionalGuard(task))
        {
            return null;
        }

        if (requireExecutableMatch && !IsTaskExecutableMatch(task))
        {
            return null;
        }

        return task;
    }

    private static dynamic? TryGetTask(object rootFolderObject, string taskName)
    {
        dynamic rootFolder = rootFolderObject;

        try
        {
            return rootFolder.GetTask(taskName);
        }
        catch (COMException ex) when (ex.HResult == FileNotFoundHResult)
        {
            return null;
        }
    }

    private static bool CanManageLegacyTask(dynamic task)
    {
        var currentExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutablePath))
        {
            return false;
        }

        var currentExecutableName = Path.GetFileName(currentExecutablePath);
        if (string.IsNullOrWhiteSpace(currentExecutableName))
        {
            return false;
        }

        try
        {
            var actionCount = (int)task.Definition.Actions.Count;
            if (actionCount < 1)
            {
                return false;
            }

            dynamic action = task.Definition.Actions.Item(1);
            var taskExecutablePath = Convert.ToString(action.Path) ?? string.Empty;
            var taskExecutableName = Path.GetFileName(taskExecutablePath);
            if (!string.Equals(taskExecutableName, currentExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var currentUserName = WindowsIdentity.GetCurrent().Name;
            var taskUserName = Convert.ToString(task.Definition.Principal.UserId) ?? string.Empty;
            return string.IsNullOrWhiteSpace(taskUserName)
                || string.Equals(taskUserName.Trim(), currentUserName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static dynamic ConnectTaskService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前系统不支持任务计划程序 COM 接口。");

        dynamic service = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("无法创建任务计划程序服务。");

        service.Connect();
        return service;
    }

    private static string SimplifyMessage(string? message)
    {
        return string.IsNullOrWhiteSpace(message) ? "未知错误。" : message.Trim();
    }
}
