using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string ListBreakpointsTool = "list_breakpoints";
    private const string DebugStartTool = "debug_start";
    private const string DebugBreakTool = "debug_break";
    private const string DebugLocalsTool = "debug_locals";
    private const string DebugThreadsTool = "debug_threads";
    private const string DebugStackTool = "debug_stack";
    private const string DebugContinueTool = "debug_continue";

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
            Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStartTool, "Start debugging after setting breakpoints"), (ListBreakpointsTool, "Verify breakpoints are registered")],
                related: [("enable_breakpoint", "Re-enable a disabled breakpoint"), ("remove_breakpoint", "Remove a breakpoint")]));

        yield return BridgeTool(ListBreakpointsTool,
            "List all breakpoints in the current debug session.",
            EmptySchema(), "list-breakpoints", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStartTool, "Start debugging"), ("enable_breakpoint", "Enable a listed breakpoint"), ("disable_breakpoint", "Disable a listed breakpoint")],
                related: [("clear_breakpoints", "Remove all breakpoints"), ("remove_breakpoint", "Remove a specific breakpoint")]));

        yield return BridgeTool("remove_breakpoint",
            "Remove a breakpoint by file and line number.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc)),
            "remove-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line))),
            Debug,
            searchHints: BuildSearchHints(
                related: [("clear_breakpoints", "Remove all breakpoints at once"), (ListBreakpointsTool, "List remaining breakpoints"), ("disable_breakpoint", "Disable instead of remove")]));

        yield return BridgeTool("clear_breakpoints",
            "Remove all breakpoints.",
            EmptySchema(), "clear-breakpoints", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(ListBreakpointsTool, "Verify breakpoints were cleared"), ("set_breakpoint", "Set a new breakpoint")]));

        yield return BridgeTool("enable_breakpoint",
            "Enable a disabled breakpoint at file/line.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc)),
            "enable-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line))),
            Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStartTool, "Start debugging after enabling")],
                related: [("disable_breakpoint", "Disable a breakpoint"), (ListBreakpointsTool, "List all breakpoints")]));

        yield return BridgeTool("disable_breakpoint",
            "Disable a breakpoint at file/line.",
            ObjectSchema(Req(FileArg, FileDesc), ReqInt(Line, LineDesc)),
            "disable-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line))),
            Debug,
            searchHints: BuildSearchHints(
                related: [("enable_breakpoint", "Re-enable the breakpoint"), (ListBreakpointsTool, "List all breakpoints"), ("remove_breakpoint", "Permanently remove instead")]));

        yield return BridgeTool("enable_all_breakpoints",
            "Enable all breakpoints.",
            EmptySchema(), "enable-all-breakpoints", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("disable_all_breakpoints", "Disable all breakpoints"), (ListBreakpointsTool, "List all breakpoints")]));

        yield return BridgeTool("disable_all_breakpoints",
            "Disable all breakpoints.",
            EmptySchema(), "disable-all-breakpoints", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("enable_all_breakpoints", "Re-enable all breakpoints"), (ListBreakpointsTool, "List all breakpoints")]));
    }

    private static IEnumerable<ToolEntry> DebugSessionTools()
    {
        yield return BridgeTool(DebugThreadsTool,
            "List debugger threads for the active debug session.",
            EmptySchema(), "debug-threads", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStackTool, "Get stack frames for a thread"), (DebugLocalsTool, "Inspect locals on a thread")],
                related: [(DebugBreakTool, "Break execution to inspect threads"), (DebugContinueTool, "Continue after inspection")]));

        yield return BridgeTool(DebugStackTool,
            "Capture stack frames for the current or selected debugger thread.",
            ObjectSchema(
                OptInt("thread_id", "Optional thread ID."),
                OptInt("max_frames", "Optional max frame count.")),
            "debug-stack",
            a => Build(
                ("thread-id", OptionalText(a, "thread_id")),
                ("max-frames", OptionalText(a, "max_frames"))),
            Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugLocalsTool, "Inspect locals at the current frame"), ("read_file", "Read source at a stack frame location")],
                related: [(DebugThreadsTool, "Switch thread context"), ("debug_watch", "Evaluate an expression at the current frame")]));

        yield return BridgeTool(DebugLocalsTool,
            "Capture local variables for the active stack frame.",
            ObjectSchema(OptInt(Max, "Optional max variable count.")),
            "debug-locals",
            a => Build((Max, OptionalText(a, Max))),
            Debug,
            searchHints: BuildSearchHints(
                workflow: [("debug_watch", "Evaluate a derived expression from locals")],
                related: [(DebugStackTool, "Navigate to a different frame"), (DebugThreadsTool, "Switch thread context")]));

        yield return BridgeTool("debug_modules",
            "Capture debugger module snapshot.",
            EmptySchema(), "debug-modules", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(DebugStackTool, "See stack frames across modules"), (DebugThreadsTool, "Inspect thread activity")]));

        yield return BridgeTool("debug_watch",
            "Evaluate one debugger watch expression in break mode.",
            ObjectSchema(Req("expression", "Expression to evaluate.")),
            "debug-watch",
            a => Build(("expression", OptionalString(a, "expression"))),
            Debug,
            searchHints: BuildSearchHints(
                related: [(DebugLocalsTool, "Inspect all locals without an expression"), (DebugStackTool, "Navigate frames before watching")]));

        yield return BridgeTool("debug_exceptions",
            "Capture debugger exception group/settings snapshot.",
            EmptySchema(), "debug-exceptions", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(DebugStartTool, "Start debugging to hit exceptions"), ("set_breakpoint", "Set a breakpoint at the exception site")]));

        yield return BridgeTool(DebugStartTool,
            "Start debugging the current startup project.",
            EmptySchema(), "debug-start", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugBreakTool, "Break execution to inspect state"), (DebugStackTool, "Capture stack after a break")],
                related: [("set_breakpoint", "Set breakpoints before starting"), ("debug_stop", "Stop the debugger")]));

        yield return BridgeTool("debug_stop",
            "Stop the debugger.",
            EmptySchema(), "debug-stop", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(DebugStartTool, "Start a new debug session"), (DebugBreakTool, "Break instead of stopping")]));

        yield return BridgeTool(DebugContinueTool,
            "Continue execution in the debugger.",
            EmptySchema(), "debug-continue", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(DebugBreakTool, "Break again after continuing"), ("debug_step_over", "Step over instead"), ("debug_step_into", "Step into instead")]));

        yield return BridgeTool(DebugBreakTool,
            "Break execution in the debugger.",
            EmptySchema(), "debug-break", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStackTool, "Inspect stack after breaking"), (DebugLocalsTool, "Inspect locals after breaking")],
                related: [(DebugContinueTool, "Resume execution"), (DebugThreadsTool, "Check all thread states")]));

        yield return BridgeTool("debug_step_over",
            "Step over the current line in the debugger.",
            EmptySchema(), "debug-step-over", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("debug_step_into", "Step into the call instead"), ("debug_step_out", "Step out of the current function"), (DebugContinueTool, "Resume to next breakpoint")]));

        yield return BridgeTool("debug_step_into",
            "Step into the current call in the debugger.",
            EmptySchema(), "debug-step-into", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("debug_step_over", "Step over instead"), ("debug_step_out", "Step out after stepping in"), (DebugContinueTool, "Resume to next breakpoint")]));

        yield return BridgeTool("debug_step_out",
            "Step out of the current function in the debugger.",
            EmptySchema(), "debug-step-out", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("debug_step_over", "Step over in the caller"), ("debug_step_into", "Step into another call"), (DebugContinueTool, "Resume to next breakpoint")]
                ));
    }
}
