using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private static IEnumerable<ToolEntry> DebugTools() =>
        BreakpointTools()
            .Concat(DebugSessionTools());

    private static IEnumerable<ToolEntry> BreakpointTools()
    {
        yield return BridgeTool("set_breakpoint",
            "Set a breakpoint at file/line with optional condition and hit count.",
            ObjectSchema(
                Req(FileArg, FileDesc),
                ReqInt(Line, LineDesc),
                OptInt(Column, "1-based column (default 1)."),
                Opt("condition", "Breakpoint condition expression."),
                OptInt("hit_count", "Hit count (default 0 = ignore).")),
            "set-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column)),
                ("condition", OptionalString(a, "condition")),
                ("hit-count", OptionalText(a, "hit_count"))),
            Debug);

        yield return BridgeTool("list_breakpoints",
            "List all breakpoints in the current debug session.",
            EmptySchema(), "list-breakpoints", _ => Empty(), Debug);

        yield return BridgeTool("remove_breakpoint",
            "Remove a breakpoint by file and line number.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc)),
            "remove-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line))),
            Debug);

        yield return BridgeTool("clear_breakpoints",
            "Remove all breakpoints.",
            EmptySchema(), "clear-breakpoints", _ => Empty(), Debug);

        yield return BridgeTool("enable_breakpoint",
            "Enable a disabled breakpoint at file/line.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc)),
            "enable-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line))),
            Debug);

        yield return BridgeTool("disable_breakpoint",
            "Disable a breakpoint at file/line.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc)),
            "disable-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line))),
            Debug);

        yield return BridgeTool("enable_all_breakpoints",
            "Enable all breakpoints.",
            EmptySchema(), "enable-all-breakpoints", _ => Empty(), Debug);

        yield return BridgeTool("disable_all_breakpoints",
            "Disable all breakpoints.",
            EmptySchema(), "disable-all-breakpoints", _ => Empty(), Debug);
    }

    private static IEnumerable<ToolEntry> DebugSessionTools()
    {
        yield return BridgeTool("debug_threads",
            "List debugger threads for the active debug session.",
            EmptySchema(), "debug-threads", _ => Empty(), Debug);

        yield return BridgeTool("debug_stack",
            "Capture stack frames for the current or selected debugger thread.",
            ObjectSchema(
                OptInt("thread_id", "Optional thread ID."),
                OptInt("max_frames", "Optional max frame count.")),
            "debug-stack",
            a => Build(
                ("thread-id", OptionalText(a, "thread_id")),
                ("max-frames", OptionalText(a, "max_frames"))),
            Debug);

        yield return BridgeTool("debug_locals",
            "Capture local variables for the active stack frame.",
            ObjectSchema(OptInt(Max, "Optional max variable count.")),
            "debug-locals",
            a => Build((Max, OptionalText(a, Max))),
            Debug);

        yield return BridgeTool("debug_modules",
            "Capture debugger module snapshot.",
            EmptySchema(), "debug-modules", _ => Empty(), Debug);

        yield return BridgeTool("debug_watch",
            "Evaluate one debugger watch expression in break mode.",
            ObjectSchema(Req("expression", "Expression to evaluate.")),
            "debug-watch",
            a => Build(("expression", OptionalString(a, "expression"))),
            Debug);

        yield return BridgeTool("debug_exceptions",
            "Capture debugger exception group/settings snapshot.",
            EmptySchema(), "debug-exceptions", _ => Empty(), Debug);

        yield return BridgeTool("debug_start",
            "Start debugging the current startup project.",
            EmptySchema(), "debug-start", _ => Empty(), Debug);

        yield return BridgeTool("debug_stop",
            "Stop the debugger.",
            EmptySchema(), "debug-stop", _ => Empty(), Debug);

        yield return BridgeTool("debug_continue",
            "Continue execution in the debugger.",
            EmptySchema(), "debug-continue", _ => Empty(), Debug);

        yield return BridgeTool("debug_break",
            "Break execution in the debugger.",
            EmptySchema(), "debug-break", _ => Empty(), Debug);

        yield return BridgeTool("debug_step_over",
            "Step over the current line in the debugger.",
            EmptySchema(), "debug-step-over", _ => Empty(), Debug);

        yield return BridgeTool("debug_step_into",
            "Step into the current call in the debugger.",
            EmptySchema(), "debug-step-into", _ => Empty(), Debug);

        yield return BridgeTool("debug_step_out",
            "Step out of the current function in the debugger.",
            EmptySchema(), "debug-step-out", _ => Empty(), Debug);
    }
}
