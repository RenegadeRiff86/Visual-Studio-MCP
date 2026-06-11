using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class DebuggerFrameContext
{
    internal DebuggerFrameContext(Thread? thread, StackFrame frame, int threadId, int frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Thread = thread;
        Frame = frame;
        ThreadId = threadId;
        FrameIndex = frameIndex;
        Function = frame.FunctionName ?? string.Empty;
        Language = frame.Language ?? string.Empty;
    }

    internal Thread? Thread { get; }

    internal StackFrame Frame { get; }

    internal int ThreadId { get; }

    internal int FrameIndex { get; }

    internal string Function { get; }

    internal string Language { get; }
}

internal static class DebuggerFrameContextResolver
{
    private const string NotInBreakModeCode = "not_in_break_mode";

    internal static DebuggerFrameContext Resolve(Debugger debugger, int? threadId, int? frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new CommandErrorException(NotInBreakModeCode, "Debugger is not currently in break mode. Call debug_break to pause execution first, then retry.");
        }

        int requestedFrameIndex = frameIndex.GetValueOrDefault(0);
        if (requestedFrameIndex < 0)
        {
            throw new CommandErrorException("frame_not_found", $"Frame index '{requestedFrameIndex}' is invalid. Call debug_stack to list valid zero-based frame indexes.");
        }

        if (!threadId.HasValue && !frameIndex.HasValue)
        {
            if (debugger.CurrentStackFrame is not StackFrame currentFrame)
            {
                throw new CommandErrorException(NotInBreakModeCode, "Debugger does not have a current stack frame. Call debug_break to pause execution first, then retry.");
            }

            Thread? currentThread = TryGetCurrentThread(debugger);
            int currentFrameIndex = currentThread is null ? 0 : FindFrameIndex(currentThread, currentFrame);
            return new DebuggerFrameContext(currentThread, currentFrame, currentThread?.ID ?? 0, currentFrameIndex);
        }

        Thread? targetThread = threadId.HasValue
            ? ResolveThread(debugger.CurrentProgram, threadId)
            : TryGetCurrentThread(debugger) ?? ResolveThread(debugger.CurrentProgram, null);
        if (targetThread is null)
        {
            string threadHint = threadId.HasValue ? $"Thread '{threadId}'" : "The current debugger thread";
            throw new CommandErrorException("thread_not_found", $"{threadHint} was not found in the current debug program. Call debug_threads to list all active thread IDs, then retry with a valid thread_id.");
        }

        StackFrame targetFrame = ResolveStackFrame(targetThread, requestedFrameIndex)
            ?? throw new CommandErrorException("frame_not_found", $"Frame index '{requestedFrameIndex}' was not found on thread '{targetThread.ID}'. Call debug_stack with that thread_id to list valid zero-based frame indexes.");

        return new DebuggerFrameContext(targetThread, targetFrame, targetThread.ID, requestedFrameIndex);
    }

    internal static void Activate(DebuggerFrameContext frameContext)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        TryInvokeComMethod(frameContext.Thread, "Activate");
        TryInvokeComMethod(frameContext.Frame, "Activate");
    }

    private static Thread? ResolveThread(Program? program, int? threadId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (program is null)
        {
            return null;
        }

        foreach (Thread thread in program.Threads)
        {
            if (threadId is null || thread.ID == threadId.Value)
            {
                return thread;
            }
        }

        return null;
    }

    private static StackFrame? ResolveStackFrame(Thread thread, int frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int index = 0;
        foreach (StackFrame stackFrame in thread.StackFrames)
        {
            if (index == frameIndex)
            {
                return stackFrame;
            }

            index++;
        }

        return null;
    }

    // Returns the thread that actually stopped at the current break. EnvDTE's Debugger.CurrentThread is
    // unreliable for native multi-threaded debugging -- when a breakpoint hits on a worker thread it has
    // been observed returning the main/UI thread, while CurrentStackFrame correctly points at the stopped
    // frame. EnvDTE exposes no StackFrame->Thread link (StackFrames.Parent is the Debugger, not the
    // Thread), so locate the thread whose call stack contains the current frame's function, preferring the
    // thread where it is the top frame (a fresh break). Only fall back to CurrentThread if that fails.
    // Internal so DebuggerService can default thread-targeting commands to the stopped thread.
    internal static Thread? TryGetCurrentThread(Debugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (debugger.CurrentStackFrame is StackFrame currentFrame
                && currentFrame.FunctionName is string functionName
                && functionName.Length > 0)
            {
                Thread? owner = FindThreadByFrameFunction(debugger.CurrentProgram, functionName);
                if (owner != null)
                {
                    return owner;
                }
            }
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Current frame owning-thread resolution failed: {ex.Message}");
        }

        try
        {
            return debugger.GetType().GetProperty("CurrentThread")?.GetValue(debugger) as Thread;
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Current debugger thread read failed: {ex.Message}");
            return null;
        }
    }

    // Finds the thread whose call stack contains a frame with the given function name. Prefers a thread
    // where it is the top frame (the stopped frame on a fresh break); otherwise returns the first thread
    // that has it anywhere in its stack. Uses typed StackFrame.FunctionName -- reflection over the COM RCW
    // does not surface these members.
    private static Thread? FindThreadByFrameFunction(Program? program, string functionName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (program is null)
        {
            return null;
        }

        Thread? deeperMatch = null;
        foreach (Thread thread in program.Threads)
        {
            int depth = FrameDepthOfFunction(thread, functionName);
            if (depth == 0)
            {
                return thread;
            }

            if (depth > 0 && deeperMatch == null)
            {
                deeperMatch = thread;
            }
        }

        return deeperMatch;
    }

    // Returns the zero-based index of the first frame whose function name matches, or -1 if none.
    private static int FrameDepthOfFunction(Thread thread, string functionName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int index = 0;
        foreach (StackFrame frame in thread.StackFrames)
        {
            if (string.Equals(frame.FunctionName, functionName, StringComparison.Ordinal))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static int FindFrameIndex(Thread thread, StackFrame targetFrame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int index = 0;
        foreach (StackFrame stackFrame in thread.StackFrames)
        {
            if (ReferenceEquals(stackFrame, targetFrame))
            {
                return index;
            }

            index++;
        }

        return 0;
    }

    private static void TryInvokeComMethod(object? target, string methodName)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            target.GetType().InvokeMember(
                methodName,
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: target,
                args: []);
        }
        catch (MissingMethodException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Debugger context activation method '{methodName}' is unavailable: {ex.Message}");
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Debugger context activation failed: {ex.Message}");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is COMException comEx)
        {
            System.Diagnostics.Debug.WriteLine($"Debugger context activation failed: {comEx.Message}");
        }
    }
}
