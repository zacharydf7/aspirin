using Aspire.Hosting;

namespace Aspirin.Hosting.Tests;

/// <summary>
/// Stands in for the generated <c>Projects.*</c> class, so a test can add a project without building one.
/// </summary>
internal sealed class TestProject : IProjectMetadata
{
    private static readonly string s_projectPath = CreateProjectFile();

    public string ProjectPath => s_projectPath;

    // AddProject refuses a project file that doesn't exist, but never reads it.
    private static string CreateProjectFile()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("aspirin-test-project-").FullName, "TestProject.csproj");
        File.WriteAllText(path, "<Project />");
        return path;
    }
}
