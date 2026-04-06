namespace VsIdeBridge.Shared;

public static partial class BridgeCommandCatalog
{
    private static bool TryGetProjectCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        switch (commandName)
        {
            case "list-projects":
                detail = ("List all projects in the open solution.", commandName);
                return true;
            case "query-project-items":
                detail = ("List items in a project with file paths, kinds, and item types.", ExampleCommand("query-project-items", @"{""project"":""VsIdeBridge"",""path"":""src\\VsIdeBridge"",""max"":200}"));
                return true;
            case "query-project-properties":
                detail = ("Read MSBuild-style project properties from one project, including normalized TargetFramework values when available.", ExampleCommand("query-project-properties", @"{""project"":""VsIdeBridge"",""names"": [""TargetFramework"", ""RootNamespace"", ""AssemblyName""]}"));
                return true;
            case "query-project-configurations":
                detail = ("List project configurations and platforms for one project.", ExampleCommand("query-project-configurations", @"{""project"":""VsIdeBridge""}"));
                return true;
            case "query-project-references":
                detail = ("List project references for one project. By default this returns resolved references with framework assemblies omitted; set declared_only=true for project-file declarations or include_framework=true for the full closure.", ExampleCommand("query-project-references", @"{""project"":""VsIdeBridge.Tests"",""declared_only"":true}"));
                return true;
            case "query-project-outputs":
                detail = ("Resolve the primary output artifact and output directory for one project using the active or requested build shape.", ExampleCommand("query-project-outputs", @"{""project"":""VsIdeBridge"",""configuration"":""Release"",""target_framework"":""net8.0""}"));
                return true;
            case "add-project":
                detail = ("Add an existing or new project to the solution.", ExampleCommand("add-project", @"{""path"":""C:\\repo\\MyLib\\MyLib.csproj""}"));
                return true;
            case "remove-project":
                detail = ("Remove a project from the solution by name or path.", ExampleCommand("remove-project", @"{""project"":""MyLib""}"));
                return true;
            case "set-startup-project":
                detail = ("Set the solution startup project by name or path.", ExampleCommand("set-startup-project", @"{""project"":""MyApp""}"));
                return true;
            case "rename-project":
                detail = ("Rename a project within the solution. This changes the project name shown by Visual Studio, but does not rename folders or the project file on disk.", ExampleCommand("rename-project", @"{""project"":""MyLib"",""new-name"":""MyLibrary""}"));
                return true;
            case "add-file-to-project":
                detail = ("Add an existing file to a project.", ExampleCommand("add-file-to-project", @"{""project"":""MyLib"",""file"":""C:\\repo\\MyLib\\Foo.cs""}"));
                return true;
            case "remove-file-from-project":
                detail = ("Remove a file from a project.", ExampleCommand("remove-file-from-project", @"{""project"":""MyLib"",""file"":""C:\\repo\\MyLib\\Foo.cs""}"));
                return true;
            case "search-solutions":
                detail = ("Search for solution files (.sln/.slnx) on disk under a given root directory. Defaults to %USERPROFILE%\\source\\repos.", ExampleCommand("search-solutions", @"{""query"":""MyApp"",""path"":""%USERPROFILE%\\source\\repos"",""max_depth"":4}"));
                return true;
            case "set-python-project-env":
                detail = ("Set the active Python interpreter for the open .pyproj project or open-folder workspace in Visual Studio (affects IntelliSense and debugging).", ExampleCommand("set-python-project-env", @"{""path"":""%USERPROFILE%\\miniconda3\\envs\\superslicer\\python.exe""}"));
                return true;
            default:
                detail = default;
                return false;
        }
    }
}
