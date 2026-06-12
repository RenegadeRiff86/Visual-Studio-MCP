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
    private const string ThreadIdArg = "thread_id";
    private const string FrameIndexArg = "frame_index";
    private const string TimeoutMsArg = "timeout_ms";
    private const string FunctionArg = "function";

    private static IEnumerable<ToolEntry> DebugTools() =>
        BreakpointTools()
            .Concat(BreakpointToggleTools())
            .Concat(DebugSessionTools());

    private static IEnumerable<ToolEntry> BreakpointTools()
    {
        yield return BridgeTool("set_breakpoint",
            "Set a breakpoint at file/line, OR at a function/symbol name (function breakpoints survive source edits and line shifts -- prefer them when iterating on the code). " +
            "Set trace_message + continue_execution to make it a tracepoint/logpoint that logs an expression and keeps running instead of stopping.",
            ObjectSchema(
                Opt(FileArg, FileDesc + " Omit when using 'function'."),
                OptInt(Line, LineDesc + " Used with 'file'."),
                OptInt(Column, "1-based column (default 1)."),
                Opt(FunctionArg, "Function/symbol name to break on (e.g. 'VsIdeBridgeService.ToolResultFormatter::StructuredToolResult' or 'MyClass::Method'). Binds by symbol so it survives line shifts; use instead of file/line."),
                Opt("condition", "Breakpoint condition expression."),
                Opt("condition_type", "How to interpret 'condition': 'when-true' (default) or 'changed'."),
                OptInt("hit_count", "Hit count target (default 0 = always break)."),
                Opt("hit_type", "Hit-count comparison: 'none' (default), 'equal', 'multiple', or 'greater-or-equal'."),
                Opt("trace_message", "Tracepoint/logpoint message logged when hit. Supports {expression} interpolation (e.g. 'id={requestId} tool={toolName}'). Pair with continue_execution=true to log without pausing."),
                OptBool("continue_execution", "When true, the breakpoint logs trace_message and continues instead of pausing (tracepoint/logpoint). Default false.")),
            "set-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (Column, OptionalText(a, Column)),
                (FunctionArg, OptionalString(a, FunctionArg)),
                ("condition", OptionalString(a, "condition")),
                ("condition-type", OptionalString(a, "condition_type")),
                ("hit-count", OptionalText(a, "hit_count")),
                ("hit-type", OptionalString(a, "hit_type")),
                ("trace-message", OptionalString(a, "trace_message")),
                ("continue-execution", OptionalText(a, "continue_execution"))),
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
            "Remove a breakpoint by file and line, OR by function/symbol name (use 'function' for a breakpoint that was set with 'function').",
            ObjectSchema(
                Opt(FileArg, FileDesc + " Omit when using 'function'."),
                OptInt(Line, LineDesc + " Used with 'file'."),
                Opt(FunctionArg, "Function/symbol name of the breakpoint to remove (as passed to set_breakpoint).")),
            "remove-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (FunctionArg, OptionalString(a, FunctionArg))),
            Debug,
            searchHints: BuildSearchHints(
                related: [("clear_breakpoints", "Remove all breakpoints at once"), (ListBreakpointsTool, "List remaining breakpoints"), ("disable_breakpoint", "Disable instead of remove")]));

        yield return BridgeTool("clear_breakpoints",
            "Remove all breakpoints.",
            EmptySchema(), "clear-breakpoints", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(ListBreakpointsTool, "Verify breakpoints were cleared"), ("set_breakpoint", "Set a new breakpoint")]));
    }

    private static IEnumerable<ToolEntry> BreakpointToggleTools()
    {
        yield return BridgeTool("enable_breakpoint",
            "Enable a disabled breakpoint at file/line, OR by function/symbol name.",
            ObjectSchema(
                Opt(FileArg, FileDesc + " Omit when using 'function'."),
                OptInt(Line, LineDesc + " Used with 'file'."),
                Opt(FunctionArg, "Function/symbol name of the breakpoint to enable (as passed to set_breakpoint).")),
            "enable-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (FunctionArg, OptionalString(a, FunctionArg))),
            Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStartTool, "Start debugging after enabling")],
                related: [("disable_breakpoint", "Disable a breakpoint"), (ListBreakpointsTool, "List all breakpoints")]));

        yield return BridgeTool("disable_breakpoint",
            "Disable a breakpoint at file/line, OR by function/symbol name.",
            ObjectSchema(
                Opt(FileArg, FileDesc + " Omit when using 'function'."),
                OptInt(Line, LineDesc + " Used with 'file'."),
                Opt(FunctionArg, "Function/symbol name of the breakpoint to disable (as passed to set_breakpoint).")),
            "disable-breakpoint",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Line, OptionalText(a, Line)),
                (FunctionArg, OptionalString(a, FunctionArg))),
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

    private static IEnumerable<ToolEntry> DebugSessionTools() =>
        DebugInspectionTools()
            .Concat(DebugExecutionTools());

    private static IEnumerable<ToolEntry> DebugInspectionTools()
    {
        yield return BridgeTool(DebugThreadsTool,
            "List debugger threads for the active debug session. In break mode each thread has an " +
            "'isCurrent' flag and the response includes 'currentThreadId' -- the thread VS selected, " +
            "i.e. the one that hit the breakpoint. Use that id with debug_stack/debug_locals to inspect the stopped frame.",
            EmptySchema(), "debug-threads", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStackTool, "Get stack frames for a thread"), (DebugLocalsTool, "Inspect locals on a thread")],
                related: [(DebugBreakTool, "Break execution to inspect threads"), (DebugContinueTool, "Continue after inspection")]));

        yield return BridgeTool(DebugStackTool,
            "Capture stack frames for a debugger thread. When thread_id is omitted, defaults to the thread " +
            "VS has selected (debugger.CurrentThread) -- on a breakpoint hit this is the thread that actually " +
            "stopped, not the process main thread. Frames include a zero-based index for debug_locals and debug_watch frame targeting.",
            ObjectSchema(
                OptInt(ThreadIdArg, "Optional thread ID. Omit to use the current/stopped thread."),
                OptInt("max_frames", "Optional max frame count.")),
            "debug-stack",
            a => Build(
                ("thread-id", OptionalText(a, ThreadIdArg)),
                ("max-frames", OptionalText(a, "max_frames"))),
            Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugLocalsTool, "Inspect locals at a returned frame index"), ("read_file", "Read source at a stack frame location")],
                related: [(DebugThreadsTool, "Choose a thread ID"), ("debug_watch", "Evaluate an expression at a returned frame index")]));

        yield return BridgeTool(DebugLocalsTool,
            "Capture local variables for the active or selected stack frame.",
            ObjectSchema(
                OptInt(Max, "Optional max variable count."),
                OptInt(ThreadIdArg, $"Optional {ThreadIdArg} from debug_threads or debug_stack."),
                OptInt(FrameIndexArg, $"Optional zero-based {FrameIndexArg} returned by debug_stack."),
                OptInt("expand_depth", "Optional depth to expand each local's child members (containers/structs). 0 = summary only (default); 1-3 walks the value tree like the Watch window, e.g. a std::vector/list to its elements."),
                OptInt("max_children", "Optional cap on child members serialized per level when expand_depth > 0 (default 50).")),
            "debug-locals",
            a => Build(
                (Max, OptionalText(a, Max)),
                ("thread-id", OptionalText(a, ThreadIdArg)),
                ("frame-index", OptionalText(a, FrameIndexArg)),
                ("expand-depth", OptionalText(a, "expand_depth")),
                ("max-children", OptionalText(a, "max_children"))),
            Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStackTool, "Get frame indexes before targeting locals"), ("debug_watch", "Evaluate a derived expression from locals")],
                related: [(DebugThreadsTool, "Choose a thread ID"), ("debug_break", "Pause execution before inspecting locals")]));

        yield return BridgeTool("debug_modules",
            "Capture debugger module snapshot.",
            EmptySchema(), "debug-modules", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(DebugStackTool, "See stack frames across modules"), (DebugThreadsTool, "Inspect thread activity")]));

        yield return BridgeTool("debug_watch",
            "Evaluate one debugger watch expression in break mode, optionally against a selected thread and stack frame. " +
            "Set expand_depth to walk the value tree (e.g. expand a std::vector/list/map or struct into its elements/fields), like expanding the node in the Watch window.",
            ObjectSchema(
                Req("expression", "Expression to evaluate."),
                OptInt(ThreadIdArg, $"Optional {ThreadIdArg} from debug_threads or debug_stack."),
                OptInt(FrameIndexArg, $"Optional zero-based {FrameIndexArg} returned by debug_stack."),
                OptInt(TimeoutMsArg, "Optional evaluation timeout in milliseconds."),
                OptInt("expand_depth", "Optional depth to expand child members (containers/structs). 0 = summary only (default); 1-3 walks the value tree like the Watch window, e.g. a std::vector/list to its elements and their fields."),
                OptInt("max_children", "Optional cap on child members serialized per level when expand_depth > 0 (default 50)."),
                OptInt("chunk_lines", "Optional. Page the expanded member tree in memory instead of returning it all inline: lines per chunk. Omit or 0 returns the full structured members[]. " +
                    "When > 0 the response returns membersJson (the selected chunk as text) plus chunkIndex/chunkCount/totalLines/hasMoreChunks; advance chunk_index to read more. Nothing is written to disk."),
                OptInt("chunk_index", "Optional zero-based chunk to return when chunk_lines > 0 (default 0).")),
            "debug-watch",
            a => Build(
                ("expression", OptionalString(a, "expression")),
                ("thread-id", OptionalText(a, ThreadIdArg)),
                ("frame-index", OptionalText(a, FrameIndexArg)),
                ("timeout-ms", OptionalText(a, TimeoutMsArg)),
                ("expand-depth", OptionalText(a, "expand_depth")),
                ("max-children", OptionalText(a, "max_children")),
                ("chunk-lines", OptionalText(a, "chunk_lines")),
                ("chunk-index", OptionalText(a, "chunk_index"))),
            Debug,
            searchHints: BuildSearchHints(
                workflow: [(DebugStackTool, "Get frame indexes before targeting a watch expression")],
                related: [(DebugLocalsTool, "Inspect all locals without an expression"), (DebugThreadsTool, "Choose a thread ID"), ("batch", "Evaluate several watch expressions in one round-trip")]));

        yield return BridgeTool("debug_exceptions",
            "Capture debugger exception group/settings snapshot, plus the last thrown or unhandled debugger " +
            "exception when one was observed. Also returns 'lastBreakReason' (e.g. dbgEventReasonBreakpoint, " +
            "dbgEventReasonExceptionThrown, dbgEventReasonUserBreak) and a 'brokeOnException' flag, so you can " +
            "tell why the debugger is stopped -- a breakpoint, a thrown/unhandled exception, a step, or a manual pause.",
            EmptySchema(), "debug-exceptions", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [(DebugStartTool, "Start debugging to hit exceptions"), ("set_breakpoint", "Set a breakpoint at the exception site")]));
    }

    private static IEnumerable<ToolEntry> DebugExecutionTools()
    {
        yield return BridgeTool(DebugStartTool,
            "Start debugging the current startup project. Responses include lastException when Visual Studio reports a thrown or unhandled exception.",
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
            "Continue execution in the debugger. Responses include lastException when Visual Studio reports a thrown or unhandled exception.",
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
            "Step over the current line in the debugger. Responses include lastException when Visual Studio reports a thrown or unhandled exception.",
            EmptySchema(), "debug-step-over", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("debug_step_into", "Step into the call instead"), ("debug_step_out", "Step out of the current function"), (DebugContinueTool, "Resume to next breakpoint")]));

        yield return BridgeTool("debug_step_into",
            "Step into the current call in the debugger. Responses include lastException when Visual Studio reports a thrown or unhandled exception.",
            EmptySchema(), "debug-step-into", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("debug_step_over", "Step over instead"), ("debug_step_out", "Step out after stepping in"), (DebugContinueTool, "Resume to next breakpoint")]));

        yield return BridgeTool("debug_step_out",
            "Step out of the current function in the debugger. Responses include lastException when Visual Studio reports a thrown or unhandled exception.",
            EmptySchema(), "debug-step-out", _ => Empty(), Debug,
            searchHints: BuildSearchHints(
                related: [("debug_step_over", "Step over in the caller"), ("debug_step_into", "Step into another call"), (DebugContinueTool, "Resume to next breakpoint")]
                ));
    }
}
