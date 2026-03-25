using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> ProjectTools()
    {
        yield return BridgeTool("list_projects",
            "List all projects in the open solution. Call before using build with project= to discover exact project names.",
            EmptySchema(), "list-projects", _ => Empty(), Project);

        yield return BridgeTool("query_project_items",
            "List items in a project with FileArg paths, kinds, and item types.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Opt(Path, "Optional path filter."),
                OptInt(Max, "Max items to return (default 200).")),
            "query-project-items",
            a => Build(
                (Project, OptionalString(a, Project)),
                (Path, OptionalString(a, Path)),
                (Max, OptionalText(a, Max))),
            Project);

        yield return BridgeTool("query_project_properties",
            "Read MSBuild project properties from one project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                OptArr("names", "Property names to read.")),
            "query-project-properties",
            a => Build(
                (Project, OptionalString(a, Project)),
                ("names", OptionalStringArray(a, "names"))),
            Project);

        yield return BridgeTool("query_project_configurations",
            "List project configurations and platforms for one project.",
            ObjectSchema(Req(Project, ProjectDesc)),
            "query-project-configurations",
            a => Build((Project, OptionalString(a, Project))),
            Project);

        yield return BridgeTool("query_project_references",
            "List project references for one project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                OptBool("declared_only", "Return only declared (project-FileArg) references."),
                OptBool("include_framework",
                    "Include framework assembly references (default false).")),
            "query-project-references",
            a => Build(
                (Project, OptionalString(a, Project)),
                BoolArg("declared-only", a, "declared_only", false, true),
                BoolArg("include-framework", a, "include_framework", false, true)),
            Project);

        yield return BridgeTool("query_project_outputs",
            "Resolve the primary output artifact and output directory for one project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Opt(Configuration, "Build configuration."),
                Opt("target_framework", "Target framework moniker.")),
            "query-project-outputs",
            a => Build(
                (Project, OptionalString(a, Project)),
                (Configuration, OptionalString(a, Configuration)),
                ("target-framework", OptionalString(a, "target_framework"))),
            Project);

        yield return BridgeTool("add_project",
            "Add an existing or new project to the solution.",
            ObjectSchema(
                Req(Project, "Absolute path to the project FileArg."),
                Opt("solution_folder", "Optional solution folder name.")),
            "add-project",
            a => Build(
                (Project, OptionalString(a, Project)),
                ("solution-folder", OptionalString(a, "solution_folder"))),
            Project);

        yield return BridgeTool("remove_project",
            "Remove a project from the solution by name or path.",
            ObjectSchema(Req(Project, "Project name or path to remove.")),
            "remove-project",
            a => Build((Project, OptionalString(a, Project))),
            Project);

        yield return BridgeTool("create_project",
            "Create a new project and add it to the open solution.",
            ObjectSchema(
                Req("name", "New project name."),
                Opt("template", "Project template name or identifier."),
                Opt("language", "Programming language (e.g. C#, VB, F#)."),
                Opt("directory", "Directory to create the project in."),
                Opt("solution_folder", "Optional solution folder name.")),
            "create-project",
            a => Build(
                ("name", OptionalString(a, "name")),
                ("template", OptionalString(a, "template")),
                ("language", OptionalString(a, "language")),
                ("directory", OptionalString(a, "directory")),
                ("solution-folder", OptionalString(a, "solution_folder"))),
            Project);

        yield return BridgeTool("set_startup_project",
            "Set the solution startup project by name or path.",
            ObjectSchema(Req(Project, ProjectDesc)),
            "set-startup-project",
            a => Build((Project, OptionalString(a, Project))),
            Project);

        yield return BridgeTool("add_file_to_project",
            "Add an existing FileArg to a project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Req(FileArg, "Absolute path to the FileArg.")),
            "add-FileArg-to-project",
            a => Build(
                (Project, OptionalString(a, Project)),
                (FileArg, OptionalString(a, FileArg))),
            Project);

        yield return BridgeTool("remove_file_from_project",
            "Remove a FileArg from a project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Req(FileArg, "FileArg path to remove.")),
            "remove-FileArg-from-project",
            a => Build(
                (Project, OptionalString(a, Project)),
                (FileArg, OptionalString(a, FileArg))),
            Project);

        yield return BridgeTool("python_set_project_env",
            "Set the active Python interpreter for the active Python project in Visual Studio.",
            ObjectSchema(
                Req(Path, "Absolute path to the Python interpreter."),
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "set-python-project-env",
            a => Build(
                (Path, OptionalString(a, Path)),
                (Project, OptionalString(a, Project))),
            Project);

        yield return BridgeTool("python_set_startup_file",
            "Set the startup FileArg for the active Python project.",
            ObjectSchema(
                Req(FileArg, "Path to the Python FileArg to set as startup."),
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "set-python-startup-FileArg",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Project, OptionalString(a, Project))),
            Project);

        yield return BridgeTool("python_get_startup_file",
            "Get the startup FileArg configured for the active Python project.",
            ObjectSchema(
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "get-python-startup-FileArg",
            a => Build((Project, OptionalString(a, Project))),
            Project);

        yield return BridgeTool("python_sync_env",
            "Sync the active bridge Python interpreter to the active Python project in Visual Studio.",
            ObjectSchema(
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "set-python-project-env",
            a => Build(
                (Path, PythonInterpreterState.LoadActiveInterpreterPath()),
                (Project, OptionalString(a, Project))),
            Project);
    }
}
