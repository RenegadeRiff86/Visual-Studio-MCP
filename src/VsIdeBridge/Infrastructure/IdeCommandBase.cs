using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using VsIdeBridge.Services;

using VsIdeBridge.Commands;

namespace VsIdeBridge.Infrastructure;

internal abstract class IdeCommandBase
{
    protected VsIdeBridgePackage Package { get; }
    protected IdeBridgeRuntime Runtime { get; }
    protected OleMenuCommand MenuCommand { get; }

    protected IdeCommandBase(
        VsIdeBridgePackage package,
        IdeBridgeRuntime runtime,
        OleMenuCommandService commandService,
        int commandId,
        bool acceptsParameters = true)
    {
        Package = package;
        Runtime = runtime;

        CommandID menuCommandId = new(CommandRegistrar.CommandSet, commandId);
        OleMenuCommand menuCommand = new(Execute, menuCommandId);
        if (acceptsParameters)
        {
            menuCommand.ParametersDescription = "$";
        }

        commandService.AddCommand(menuCommand);
        MenuCommand = menuCommand;
    }

    protected abstract string CanonicalName { get; }

    internal string Name => CanonicalName;

    internal virtual bool AllowAutomationInvocation => true;

    internal Task<CommandExecutionResult> ExecuteDirectAsync(IdeCommandContext ctx, CommandArguments args)
        => ExecuteAsync(ctx, args);

    protected abstract Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args);

    private void Execute(object sender, EventArgs e)
    {
        _ = Package.JoinableTaskFactory.RunAsync(() => ExecuteInternalAsync(e));
    }

    private async Task ExecuteInternalAsync(EventArgs e)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        string? rawArguments = (e as OleMenuCmdEventArgs)?.InValue as string;
        string outputPath = string.Empty;
        string? requestId = null;

        await Package.JoinableTaskFactory.SwitchToMainThreadAsync(Package.DisposalToken);
        DTE2? dte = await Package.GetServiceAsync(typeof(SDTE)) as DTE2;
        Assumes.Present(dte);

        IdeCommandContext context = new(Package, dte, Runtime.Logger, Runtime, Package.DisposalToken);
        await Task.Factory.StartNew(
            static () => { },
            System.Threading.CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default).ConfigureAwait(false);

        try
        {
            CommandArguments args = CommandArgumentParser.Parse(rawArguments);
            outputPath = ResolveOutputPath(args);
            requestId = args.GetString("request-id");
            CommandExecutionResult commandResult = await ExecuteAsync(context, args).ConfigureAwait(false);
            CommandEnvelope envelope = new()
            {
                SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
                Command = CanonicalName,
                RequestId = requestId,
                Success = true,
                StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
                FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                Summary = commandResult.Summary,
                Warnings = commandResult.Warnings,
                Error = null,
                Data = commandResult.Data,
            };

            await CommandResultWriter.WriteAsync(outputPath, envelope, Package.DisposalToken).ConfigureAwait(false);
            await context.Logger.LogAsync($"IDE Bridge: {CanonicalName} OK - {commandResult.Summary} -> {outputPath}", Package.DisposalToken, activatePane: true).ConfigureAwait(false);
        }
        catch (CommandErrorException ex)
        {
            await HandleCommandErrorAsync(context, ex, requestId, outputPath, startedAt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null) // top-level exception boundary; all unexpected exceptions are handled here
        {
            await HandleUnexpectedExceptionAsync(context, ex, requestId, outputPath, startedAt).ConfigureAwait(false);
        }
    }

    private string ResolveOutputPath(CommandArguments args)
    {
        string? explicitPath = args.GetString("out");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath!;
        }

        string fileName = CanonicalName.Replace("Tools.", string.Empty)
            .Replace('.', '-')
            .ToLowerInvariant() + ".json";
        return Path.Combine(Path.GetTempPath(), "vs-ide-bridge", fileName);
    }

    private async Task HandleCommandErrorAsync(IdeCommandContext context, CommandErrorException ex, string? requestId, string outputPath, DateTimeOffset startedAt)
    {
        Newtonsoft.Json.Linq.JToken? failureData = await Runtime.FailureContextService.CaptureAsync(context).ConfigureAwait(false);
        CommandEnvelope envelope = new()
        {
            SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
            Command = CanonicalName,
            RequestId = requestId,
            Success = false,
            StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
            FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            Summary = ex.Message,
            Warnings = [],
            Error = new { code = ex.Code, message = ex.Message, details = ex.Details },
            Data = failureData,
        };
        if (!string.IsNullOrWhiteSpace(outputPath))
            await CommandResultWriter.WriteAsync(outputPath, envelope, Package.DisposalToken).ConfigureAwait(false);
        await context.Logger.LogAsync($"IDE Bridge: {CanonicalName} FAIL - {ex.Code}", Package.DisposalToken, activatePane: true).ConfigureAwait(false);
        ActivityLog.LogError(nameof(VsIdeBridgePackage), ex.ToString());
    }

    private async Task HandleUnexpectedExceptionAsync(IdeCommandContext context, Exception ex, string? requestId, string outputPath, DateTimeOffset startedAt)
    {
        Newtonsoft.Json.Linq.JToken? failureData = await Runtime.FailureContextService.CaptureAsync(context).ConfigureAwait(false);
        CommandEnvelope envelope = new()
        {
            SchemaVersion = JsonSchemaVersioning.CurrentSchemaVersion,
            Command = CanonicalName,
            RequestId = requestId,
            Success = false,
            StartedAtUtc = startedAt.UtcDateTime.ToString("O"),
            FinishedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            Summary = ex.Message,
            Warnings = [],
            Error = new { code = "internal_error", message = ex.Message, details = new { exception = ex.ToString() } },
            Data = failureData,
        };
        if (!string.IsNullOrWhiteSpace(outputPath))
            await CommandResultWriter.WriteAsync(outputPath, envelope, Package.DisposalToken).ConfigureAwait(false);
        await context.Logger.LogAsync($"IDE Bridge: {CanonicalName} FAIL - internal_error", Package.DisposalToken, activatePane: true).ConfigureAwait(false);
        ActivityLog.LogError(nameof(VsIdeBridgePackage), ex.ToString());
    }
}
