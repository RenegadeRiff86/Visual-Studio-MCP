using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Diagnostics;
using static VsIdeBridge.Diagnostics.ErrorListConstants;

namespace VsIdeBridge.Services;

internal sealed class ErrorListQuery
{
    public string? Severity { get; set; }
    public string? Code { get; set; }
    public string? Project { get; set; }
    public string? Path { get; set; }
    public string? Text { get; set; }
    public string? GroupBy { get; set; }
    public int? Max { get; set; }
    public JObject ToJson()
    {
        return new JObject
        {
            ["severity"] = Severity ?? string.Empty,
            ["code"] = Code ?? string.Empty,
            ["project"] = Project ?? string.Empty,
            ["path"] = Path ?? string.Empty,
            ["text"] = Text ?? string.Empty,
            ["groupBy"] = GroupBy ?? string.Empty,
            ["max"] = (JToken?)Max ?? JValue.CreateNull(),
        };
    }
}

internal sealed partial class ErrorListService
{
    private sealed class ErrorTableCollector : ITableDataSink, IDisposable
    {
        private readonly List<ITableEntry> _entries = [];
        private readonly List<ITableEntriesSnapshot> _snapshots = [];
        private readonly List<ITableEntriesSnapshotFactory> _factories = [];

        public bool IsStable { get; set; } = true;

        public bool HasData => _entries.Count > 0 || _snapshots.Count > 0 || _factories.Count > 0;

        public IReadOnlyList<JObject> GetRows()
        {
            List<JObject> rows = new List<JObject>();

            foreach (ITableEntry entry in _entries)
            {
                rows.Add(CreateRowFromTableEntry(entry));
            }

            foreach (ITableEntriesSnapshot snapshot in _snapshots)
            {
                AddSnapshotRows(rows, snapshot);
            }

            foreach (ITableEntriesSnapshotFactory factory in _factories)
            {
                AddSnapshotRows(rows, factory.GetCurrentSnapshot());
            }

            return [.. rows
                .GroupBy(CreateDiagnosticIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(SelectPreferredDiagnosticRow)];
        }

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries)
        {
            if (removeAllEntries)
            {
                _entries.Clear();
            }

            _entries.AddRange(newEntries);
        }

        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
        {
            foreach (ITableEntry entry in oldEntries)
            {
                _entries.Remove(entry);
            }
        }

        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
        {
            RemoveEntries(oldEntries);
            AddEntries(newEntries, removeAllEntries: false);
        }

        public void RemoveAllEntries()
        {
            _entries.Clear();
        }

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots)
        {
            if (removeAllSnapshots)
            {
                _snapshots.Clear();
            }

            _snapshots.Add(newSnapshot);
        }

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
        {
            _snapshots.Remove(oldSnapshot);
        }

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
        {
            RemoveSnapshot(oldSnapshot);
            AddSnapshot(newSnapshot, removeAllSnapshots: false);
        }

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories)
        {
            if (removeAllFactories)
            {
                _factories.Clear();
            }

            _factories.Add(newFactory);
        }

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
        {
            _factories.Remove(oldFactory);
        }

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
        {
            RemoveFactory(oldFactory);
            AddFactory(newFactory, removeAllFactories: false);
        }

        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory? factory)
        {
        }

        public void RemoveAllFactories()
        {
            _factories.Clear();
        }

        public void RemoveAllSnapshots()
        {
            _snapshots.Clear();
        }

        public void Dispose()
        {
        }

        private static void AddSnapshotRows(List<JObject> rows, ITableEntriesSnapshot snapshot)
        {
            for (int index = 0; index < snapshot.Count; index++)
            {
                rows.Add(CreateRowFromTableSnapshot(snapshot, index));
            }
        }
    }

    private sealed class BestPracticeTableDataSource : ITableDataSource
    {
        private readonly BestPracticeSnapshotFactory _factory = new();
        private ITableDataSink? _sink;

        public string DisplayName => "VS IDE Bridge Best Practices";

        public string Identifier { get; } = $"vs-ide-bridge-best-practice-{Guid.NewGuid():N}";

        public string SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;

        public IDisposable Subscribe(ITableDataSink sink)
        {
            _sink = sink;
            sink.AddFactory(_factory, removeAllFactories: false);
            return new Subscription(this, sink);
        }

        public void UpdateRows(IReadOnlyList<JObject> rows)
        {
            _factory.UpdateRows(rows);
            _sink?.FactorySnapshotChanged(_factory);
        }

        private void Unsubscribe(ITableDataSink sink)
        {
            if (!ReferenceEquals(_sink, sink))
            {
                return;
            }

            sink.RemoveFactory(_factory);
            _sink = null;
        }

        private sealed class Subscription(BestPracticeTableDataSource owner, ITableDataSink sink) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                owner.Unsubscribe(sink);
                _disposed = true;
            }
        }
    }

    private sealed class BestPracticeSnapshotFactory : ITableEntriesSnapshotFactory
    {
        private BestPracticeTableEntriesSnapshot _current = new([], 0);

        public int CurrentVersionNumber => _current.VersionNumber;

        public ITableEntriesSnapshot GetCurrentSnapshot() => _current;

        public ITableEntriesSnapshot GetSnapshot(int versionNumber)
        {
            return _current;
        }

        public void UpdateRows(IReadOnlyList<JObject> rows)
        {
            BestPracticeTableEntry[] entries = rows.Select(BestPracticeTableEntry.FromRow).ToArray();
            _current = new BestPracticeTableEntriesSnapshot(entries, _current.VersionNumber + 1);
        }

        public void Dispose()
        {
        }
    }

    private sealed class BestPracticeTableEntriesSnapshot(IReadOnlyList<BestPracticeTableEntry> entries, int versionNumber) : ITableEntriesSnapshot
    {
        private readonly IReadOnlyList<BestPracticeTableEntry> _entries = entries;
        private readonly Dictionary<string, int> _entryIndexes = entries
            .Select((entry, index) => new KeyValuePair<string, int>(entry.StableKey, index))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        public int Count => _entries.Count;

        public int VersionNumber { get; } = versionNumber;

        public int IndexOf(int currentIndex, ITableEntriesSnapshot newSnapshot)
        {
            if ((uint)currentIndex >= (uint)_entries.Count || newSnapshot is not BestPracticeTableEntriesSnapshot typedSnapshot)
            {
                return -1;
            }

            return typedSnapshot._entryIndexes.TryGetValue(_entries[currentIndex].StableKey, out int newIndex)
                ? newIndex
                : -1;
        }

        public void StartCaching()
        {
        }

        public void StopCaching()
        {
        }

        public bool TryGetValue(int index, string keyName, out object content)
        {
            if ((uint)index >= (uint)_entries.Count)
            {
                content = null!;
                return false;
            }

            BestPracticeTableEntry entry = _entries[index];
            switch (keyName)
            {
                case StandardTableKeyNames.ErrorSeverity:
                    content = MapVisualStudioErrorCategory(entry.Severity);
                    return true;
                case StandardTableKeyNames.ErrorCode:
                case StandardTableKeyNames.ErrorCodeToolTip:
                    content = entry.Code;
                    return true;
                case StandardTableKeyNames.Text:
                    content = entry.Message;
                    return true;
                case StandardTableKeyNames.DocumentName:
                    content = Path.GetFileName(entry.File);
                    return true;
                case StandardTableKeyNames.Path:
                    content = entry.File;
                    return true;
                case StandardTableKeyNames.Line:
                    content = Math.Max(0, entry.Line - 1);
                    return true;
                case StandardTableKeyNames.Column:
                    content = Math.Max(0, entry.Column - 1);
                    return true;
                case StandardTableKeyNames.ProjectName:
                    content = entry.Project;
                    return true;
                case StandardTableKeyNames.BuildTool:
                    content = entry.Tool;
                    return true;
                case StandardTableKeyNames.ErrorSource:
                    content = Microsoft.VisualStudio.Shell.TableManager.ErrorSource.Build;
                    return true;
                case StandardTableKeyNames.HelpKeyword:
                    content = string.IsNullOrWhiteSpace(entry.Code) ? BestPracticeCategory : entry.Code;
                    return true;
                case StandardTableKeyNames.HelpLink:
                    content = entry.HelpUri;
                    return true;
                case GuidanceKey:
                    content = entry.Guidance;
                    return true;
                case SuggestedActionKey:
                    content = entry.SuggestedAction;
                    return true;
                case LlmFixPromptKey:
                    content = entry.LlmFixPrompt;
                    return true;
                case AuthorityKey:
                    content = entry.Authority;
                    return true;
                case StandardTableKeyNames.FullText:
                    content = string.IsNullOrWhiteSpace(entry.Code) ? entry.Message : $"{entry.Code}: {entry.Message}";
                    return true;
                default:
                    content = null!;
                    return false;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class BestPracticeTableEntry(string severity, string code, string message, string file, int line, int column, string project, string tool, string helpUri, string guidance, string suggestedAction, string llmFixPrompt, string authority)
    {
        public string Severity { get; } = severity;

        public string Code { get; } = code;

        public string Message { get; } = message;

        public string File { get; } = file;

        public int Line { get; } = line;

        public int Column { get; } = column;

        public string Project { get; } = project;

        public string Tool { get; } = tool;

        public string HelpUri { get; } = helpUri;

        public string Guidance { get; } = guidance;

        public string SuggestedAction { get; } = suggestedAction;

        public string LlmFixPrompt { get; } = llmFixPrompt;

        public string Authority { get; } = authority;

        public string StableKey => string.Join("|", Severity, Code, File, Line.ToString(CultureInfo.InvariantCulture), Column.ToString(CultureInfo.InvariantCulture), Message);

        public static BestPracticeTableEntry FromRow(JObject row)
        {
            return new BestPracticeTableEntry(
                string.IsNullOrEmpty(GetRowString(row, SeverityKey)) ? WarningSeverity : GetRowString(row, SeverityKey),
                GetRowString(row, CodeKey),
                GetRowString(row, MessageKey),
                GetRowString(row, FileKey),
                Math.Max(1, GetNullableRowInt(row, LineKey) ?? 1),
                Math.Max(1, GetNullableRowInt(row, ColumnKey) ?? 1),
                GetRowString(row, ProjectKey),
                string.IsNullOrEmpty(GetRowString(row, ToolKey)) ? BestPracticeCategory : GetRowString(row, ToolKey),
                GetRowString(row, HelpUriKey),
                GetRowString(row, GuidanceKey),
                GetRowString(row, SuggestedActionKey),
                GetRowString(row, LlmFixPromptKey),
                GetRowString(row, AuthorityKey));
        }
    }
}
