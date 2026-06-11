# Visual Studio Debugger SDK Notes

These notes capture official Microsoft guidance that matters for the bridge debug tools.

## Main Takeaway

- `EnvDTE.Debugger.Breakpoints.Add(...)` creates a breakpoint request, but that does not guarantee the breakpoint is already bound to executable code.
- In the Visual Studio debugger model, a breakpoint can be `pending`, `bound`, or `error`.
- The bridge should not treat "created but not yet resolved" as the same thing as "failed to create".
- `EnvDTE.DebuggerEvents.OnExceptionThrown` and `OnExceptionNotHandled` are the DTE events to capture model-visible exception details from a running debug session.

## Relevant APIs

- `EnvDTE.Debugger.Breakpoints.Add(...)`
  - High-level automation entry point for source breakpoints.
  - Supports file, line, column, condition, and hit count.
- `EnvDTE80.Debugger2.Breakpoints`
  - Lets the extension inspect the current breakpoint collection after adding one.
- `EnvDTE.DebuggerEvents.OnExceptionThrown`
  - Reports exceptions thrown while the debugger is running.
  - Use this to populate `lastException` in debugger tool responses when Visual Studio observes a thrown exception.
- `EnvDTE.DebuggerEvents.OnExceptionNotHandled`
  - Reports exceptions that Visual Studio sees as not handled.
  - Use this to update `lastException` with the stronger `notHandled` event kind.
- `IDebugBreakpointBoundEvent2`
  - Reports that a pending breakpoint has bound to code.
- `IDebugBreakpointErrorEvent2`
  - Reports that a breakpoint failed to bind.
- `IDebugPendingBreakpoint2`
  - Represents the requested breakpoint before binding completes.

## What This Means For The Bridge

- `set_breakpoint` should distinguish these states:
  - created and already bound
  - created but still pending
  - created but in error
- Returning `internal_error` just because the DTE collection lookup did not immediately find a resolved breakpoint is too aggressive.
- This is especially important for C++ solutions, where modules may not be loaded yet and some source lines are not executable.
- Debugger run and step tools should expose `lastException` when the VS debugger reports a thrown or unhandled exception.
- `debug_exceptions` is the explicit follow-up tool models should call when they need the current exception settings snapshot plus the latest captured exception.

## Practical Guidance For This Repo

- Keep the current DTE add path as the lightweight request path.
- Change the post-add verification logic to report a pending/unresolved status instead of throwing.
- If the bridge needs production-grade breakpoint status, move to the lower-level debugger event model and surface bound/error details explicitly.
- Keep `DebuggerEvents` objects alive on the tracker/service; DTE event sinks can stop firing if the event object is only held in a temporary local.
- Clear `lastException` when execution enters run mode so models do not mistake a previous exception for the current run.

## Official References

- DebuggerEvents Interface (EnvDTE)
  - https://learn.microsoft.com/en-us/dotnet/api/envdte.debuggerevents?view=visualstudiosdk-2022
- Events.DebuggerEvents Property (EnvDTE)
  - https://learn.microsoft.com/en-us/dotnet/api/envdte.events.debuggerevents?view=visualstudiosdk-2022
- DebuggerEventsClass.OnExceptionNotHandled Event (EnvDTE)
  - https://learn.microsoft.com/en-us/dotnet/api/envdte.debuggereventsclass.onexceptionnothandled?view=visualstudiosdk-2015
- Breakpoints.Add Method (EnvDTE)
  - https://learn.microsoft.com/en-us/dotnet/api/envdte.breakpoints.add?view=visualstudiosdk-2022
- Debugger2.Breakpoints Property
  - https://learn.microsoft.com/en-us/dotnet/api/envdte80.debugger2.breakpoints?view=visualstudiosdk-2022
- Breakpoints (Visual Studio SDK)
  - https://learn.microsoft.com/en-us/visualstudio/extensibility/debugger/breakpoints-visual-studio-sdk?view=vs-2022
- Bind breakpoints
  - https://learn.microsoft.com/en-us/visualstudio/extensibility/debugger/binding-breakpoints?view=visualstudio
- Breakpoint errors
  - https://learn.microsoft.com/en-us/visualstudio/extensibility/debugger/breakpoint-errors?view=visualstudio
