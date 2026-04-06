using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal enum BridgeApprovalKind
{
    ShellExec,
    PythonExecution,
    PythonEnvironmentMutation,
    Build,
}

internal sealed class BridgeApprovalService
{
    public async Task<JObject> RequestApprovalAsync(IdeCommandContext context, BridgeApprovalKind kind, string? subject, string? details)
    {
        if (IsAllowed(context.Runtime.UiSettings, kind))
        {
            return CreateApprovalData(kind, approval: GetApprovalMode(kind), approvalChoice: "settings", promptShown: false, resultCode: 0);
        }

        await context.Logger.LogAsync(
            $"IDE Bridge: blocked {GetOperationDisplayName(kind)} because interactive approval prompts are disabled.",
            context.CancellationToken,
            activatePane: true).ConfigureAwait(true);

        throw new CommandErrorException(
            GetDeniedCode(kind),
            GetDeniedMessage(kind),
            new
            {
                approvalRequested = true,
                approvalChoice = "blocked_by_settings",
                promptShown = false,
                operation = GetOperationCode(kind),
                persistentSettingEnabled = false,
                subject,
                details,
            });
    }

    private static bool IsAllowed(BridgeUiSettingsService settings, BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Build => true,
            BridgeApprovalKind.ShellExec => settings.AllowBridgeShellExec,
            BridgeApprovalKind.PythonExecution => settings.AllowBridgePythonExecution,
            BridgeApprovalKind.PythonEnvironmentMutation => settings.AllowBridgePythonEnvironmentMutation,
            _ => false,
        };
    }

    private static string GetApprovalMode(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.Build => "implicit",
            _ => "persistent",
        };
    }

    private static JObject CreateApprovalData(BridgeApprovalKind kind, string approval, string approvalChoice, bool promptShown, int resultCode)
    {
        return new JObject
        {
            ["operation"] = GetOperationCode(kind),
            ["approval"] = approval,
            ["approvalChoice"] = approvalChoice,
            ["promptShown"] = promptShown,
            ["persistentSettingEnabled"] = string.Equals(approval, "persistent", StringComparison.Ordinal),
            ["resultCode"] = resultCode,
        };
    }

    private static string GetDeniedCode(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.ShellExec => "shell_exec_approval_denied",
            BridgeApprovalKind.PythonExecution => "python_exec_approval_denied",
            BridgeApprovalKind.PythonEnvironmentMutation => "python_env_mutation_approval_denied",
            BridgeApprovalKind.Build => "build_approval_denied",
            _ => "approval_denied",
        };
    }

    private static string GetDeniedMessage(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.ShellExec => "Bridge shell exec is blocked because interactive approval prompts are disabled and this capability is not enabled in IDE Bridge settings.",
            BridgeApprovalKind.PythonExecution => "Bridge Python execution is blocked because interactive approval prompts are disabled and this capability is not enabled in IDE Bridge settings.",
            BridgeApprovalKind.PythonEnvironmentMutation => "Bridge Python environment mutation is blocked because interactive approval prompts are disabled and this capability is not enabled in IDE Bridge settings.",
            BridgeApprovalKind.Build => "Bridge build approval is disabled.",
            _ => "Bridge approval was denied.",
        };
    }

    private static string GetOperationDisplayName(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.ShellExec => "run an external process from this solution",
            BridgeApprovalKind.PythonExecution => "execute Python using the selected interpreter",
            BridgeApprovalKind.PythonEnvironmentMutation => "modify a Python environment",
            BridgeApprovalKind.Build => "build the solution",
            _ => "perform a privileged action",
        };
    }

    private static string GetOperationCode(BridgeApprovalKind kind)
    {
        return kind switch
        {
            BridgeApprovalKind.ShellExec => "shell_exec",
            BridgeApprovalKind.PythonExecution => "python_exec",
            BridgeApprovalKind.PythonEnvironmentMutation => "python_env_mutation",
            BridgeApprovalKind.Build => "build",
            _ => "unknown",
        };
    }
}
