using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

internal static class WindowsStartup
{
    public const string Argument = "--windows-startup";
    private const int FileNotFound = unchecked((int)0x80070002);

    public static bool IsEnabled(string executable)
    {
        using (var scheduler = new Scheduler())
        {
            dynamic task = scheduler.Find();
            if (task == null || !task.Enabled) return false;
            dynamic actions = task.Definition.Actions;
            if (actions.Count != 1 || actions.Item(1).Type != 0) return false;
            dynamic action = actions.Item(1);
            return string.Equals((string)action.Path, executable, StringComparison.OrdinalIgnoreCase)
                && string.Equals((string)action.Arguments, Argument, StringComparison.Ordinal);
        }
    }

    public static void SetEnabled(bool enabled, string executable)
    {
        using (var scheduler = new Scheduler())
        {
            if (enabled) scheduler.Register(executable);
            else if (scheduler.Find() != null) scheduler.Folder.DeleteTask(scheduler.Name, 0);
        }
    }

    private sealed class Scheduler : IDisposable
    {
        private readonly dynamic service;
        private readonly string user;
        public readonly dynamic Folder;
        public string Name { get { return Program.Name + " (" + user + ")"; } }

        public Scheduler()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                user = identity.User.Value;
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            try
            {
                service.Connect();
                Folder = service.GetFolder(@"\");
            }
            catch
            {
                Marshal.ReleaseComObject(service);
                throw;
            }
        }

        public dynamic Find()
        {
            try { return Folder.GetTask(Name); }
            catch (FileNotFoundException) { return null; }
            catch (COMException error)
            {
                if (error.ErrorCode != FileNotFound) throw;
                return null;
            }
        }

        public void Register(string executable)
        {
            const int interactiveToken = 3;
            const int logonTrigger = 9;
            const int executableAction = 0;
            const int createOrUpdate = 6;
            const int highestPrivileges = 1;
            const int ignoreNewInstance = 2;
            dynamic task = service.NewTask(0);
            task.RegistrationInfo.Description = "Start Omnissiah Breakwall when this user signs in.";
            task.Principal.UserId = user;
            task.Principal.LogonType = interactiveToken;
            task.Principal.RunLevel = highestPrivileges;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.MultipleInstances = ignoreNewInstance;
            task.Triggers.Create(logonTrigger).UserId = user;
            dynamic action = task.Actions.Create(executableAction);
            action.Path = executable;
            action.Arguments = Argument;
            action.WorkingDirectory = Path.GetDirectoryName(executable);
            Folder.RegisterTaskDefinition(Name, task, createOrUpdate, user, null, interactiveToken, null);
        }

        public void Dispose()
        {
            Marshal.ReleaseComObject(Folder);
            Marshal.ReleaseComObject(service);
        }
    }
}
