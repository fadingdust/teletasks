using TeleTasks.Discovery.Detectors;

namespace TeleTasks.Discovery;

public static class ProjectDiscoverer
{
    public static IEnumerable<TaskCandidate> Discover(string projectPath)
    {
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException($"Project path not found: {projectPath}");
        }

        var absolute = Path.GetFullPath(projectPath);

        return MakefileDetector.Detect(absolute)
            .Concat(JustfileDetector.Detect(absolute))
            .Concat(PackageJsonDetector.Detect(absolute))
            .Concat(PyprojectDetector.Detect(absolute))
            .Concat(VsCodeTasksDetector.Detect(absolute))
            .Concat(ShellScriptDetector.Detect(absolute));
    }
}
