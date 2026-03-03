using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace VsIdeBridge.Services;

[Export(typeof(EditorFormatDefinition))]
[Name("VsIdeBridgeChangedLine")]
[UserVisible(true)]
internal sealed class BridgeChangedLineFormatDefinition : MarkerFormatDefinition
{
    public BridgeChangedLineFormatDefinition()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0x2B, 0x4A, 0x2A);
        ForegroundColor = System.Windows.Media.Colors.White;
        DisplayName = "VS IDE Bridge Changed Lines";
        ZOrder = 6;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name("VsIdeBridgeDeletedLine")]
[UserVisible(true)]
internal sealed class BridgeDeletedLineFormatDefinition : MarkerFormatDefinition
{
    public BridgeDeletedLineFormatDefinition()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0x5A, 0x22, 0x22);
        ForegroundColor = System.Windows.Media.Colors.White;
        DisplayName = "VS IDE Bridge Deleted Line Markers";
        ZOrder = 7;
    }
}

[Export(typeof(IViewTaggerProvider))]
[ContentType("text")]
[TagType(typeof(TextMarkerTag))]
internal sealed class BridgeEditHighlightTaggerProvider : IViewTaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView.TextBuffer != buffer)
        {
            return null;
        }

        return new BridgeEditHighlightTagger(buffer) as ITagger<T>;
    }

    private sealed class BridgeEditHighlightTagger : ITagger<TextMarkerTag>
    {
        private readonly ITextBuffer _buffer;

        public BridgeEditHighlightTagger(ITextBuffer buffer)
        {
            _buffer = buffer;
            BridgeEditHighlightService.Instance.HighlightsChanged += OnHighlightsChanged;
        }

        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0)
            {
                yield break;
            }

            var snapshot = spans[0].Snapshot;
            foreach (var (span, markerType) in BridgeEditHighlightService.Instance.GetHighlights(snapshot))
            {
                if (spans.IntersectsWith(span))
                {
                    yield return new TagSpan<TextMarkerTag>(span, new TextMarkerTag(markerType));
                }
            }
        }

        private void OnHighlightsChanged(object? sender, SnapshotSpanEventArgs e)
        {
            if (e.SnapshotSpan.Snapshot.TextBuffer == _buffer)
            {
                TagsChanged?.Invoke(this, e);
            }
        }
    }
}
