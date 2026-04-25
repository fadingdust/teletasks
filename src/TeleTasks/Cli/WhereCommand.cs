using TeleTasks.Configuration;

namespace TeleTasks.Cli;

public static class WhereCommand
{
    public static int Run(string[] args)
    {
        var configDir = UserConfigDirectory.Resolve();
        var local = UserConfigDirectory.LocalSettingsPath;
        var tasks = UserConfigDirectory.TasksPath;

        Console.WriteLine("Resolved paths:");
        Console.WriteLine();
        Console.WriteLine($"  config dir              : {configDir}");
        Console.WriteLine($"  appsettings.Local.json  : {local}  {Mark(local)}");
        Console.WriteLine($"  tasks.json              : {tasks}  {Mark(tasks)}");
        Console.WriteLine();
        Console.WriteLine("Source resolution order:");
        Console.WriteLine($"  1. $TELETASKS_CONFIG_DIR    = {Show("TELETASKS_CONFIG_DIR")}");
        Console.WriteLine($"  2. $XDG_CONFIG_HOME         = {Show("XDG_CONFIG_HOME")}");
        Console.WriteLine($"  3. SpecialFolder.ApplicationData = {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}");
        Console.WriteLine($"  4. $HOME                    = {Show("HOME")}");
        Console.WriteLine($"  5. SpecialFolder.UserProfile = {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
        Console.WriteLine($"  6. AppContext.BaseDirectory = {AppContext.BaseDirectory}");
        return 0;
    }

    private static string Mark(string path) =>
        File.Exists(path) ? "(exists)" : "(missing)";

    private static string Show(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? "(unset)" : v;
    }
}
