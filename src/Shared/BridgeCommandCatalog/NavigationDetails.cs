namespace VsIdeBridge.Shared;

public static partial class BridgeCommandCatalog
{
    private const string ExampleFilePath = @"C:\repo\src\foo.cpp";

    private static bool TryGetNavigationCommandDetail(string commandName, out (string Description, string Example) detail)
    {
        switch (commandName)
        {
            case "find-text":
                detail = ("Find text across the solution, project, or current document, with optional subtree filtering.", ExampleCommand("find-text", @"{""query"":""OnInit"",""path"":""src\\libslic3r""}"));
                return true;
            case "find-text-batch":
                detail = ("Find text for multiple queries in one bridge round-trip, internally chunked when needed.", ExampleCommand("find-text-batch", @"{""queries"": [""OnInit"", ""RunAsync"", ""BridgeHealth""], ""path"": ""src\\VsIdeBridge"", ""max_queries_per_chunk"": 5}"));
                return true;
            case "find-files":
                detail = ("Search Solution Explorer-style files by name or path fragment and return ranked matches.", ExampleCommand("find-files", @"{""query"":""CMakeLists.txt""}"));
                return true;
            case "find-references":
                detail = ("Run Find All References for the symbol at a file, line, and column.", ExampleCommand("find-references", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "count-references":
                detail = ("Run Find All References and return exact count when Visual Studio exposes one, or explicit unknown otherwise.", ExampleCommand("count-references", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "call-hierarchy":
                detail = ("Open Call Hierarchy for the symbol at a file, line, and column. For managed languages, also return a bounded caller tree in the command result.", ExampleCommand("call-hierarchy", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13,""max_depth"":2}"));
                return true;
            case "smart-context":
                detail = ("Collect focused code context for a natural-language query.", ExampleCommand("smart-context", @"{""query"":""where is GUI_App::OnInit used"",""max_contexts"":3}"));
                return true;
            case "goto-definition":
                detail = ("Navigate to the definition of the symbol at a file, line, and column.", ExampleCommand("goto-definition", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "goto-implementation":
                detail = ("Navigate to one implementation of the symbol at a file, line, and column.", ExampleCommand("goto-implementation", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            case "file-outline":
                detail = ("List a file outline from the code model.", ExampleCommand("file-outline", @"{""file"":""" + ExampleFilePath + @"""}"));
                return true;
            case "file-symbols":
                detail = ("List symbols in one file with optional kind filtering.", ExampleCommand("file-symbols", @"{""file"":""" + ExampleFilePath + @""",""kind"":""function""}"));
                return true;
            case "search-symbols":
                detail = ("Search symbol definitions by name across solution scope.", ExampleCommand("search-symbols", @"{""query"":""RunAsync"",""kind"":""function"",""path"":""src\\VsIdeBridge""}"));
                return true;
            case "quick-info":
                detail = ("Resolve symbol information at file, line, and column with surrounding context.", ExampleCommand("quick-info", @"{""file"":""" + ExampleFilePath + @""",""line"":42,""column"":13}"));
                return true;
            default:
                detail = default;
                return false;
        }
    }
}
