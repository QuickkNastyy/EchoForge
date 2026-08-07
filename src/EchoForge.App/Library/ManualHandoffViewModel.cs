using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using EchoForge.Core.Exports;
using EchoForge.Core.ManualCopy;

namespace EchoForge.App.Library;

/// <summary>
/// The manual-handoff dialog's logic, separated from its window so the copy, the save, the counts and
/// the privacy boundary can be tested without a screen.
///
/// <para>
/// It never copies anything on its own. Opening the dialog composes a preview and shows it; the text
/// reaches the clipboard or a file only when the user runs <see cref="Copy"/> or <see cref="Save"/>.
/// The preview is editable, and those edits are the redaction: they change what is copied and saved,
/// and never touch the canonical transcript. The counts always describe the current preview, so what
/// the user sees is exactly what leaves.
/// </para>
/// </summary>
public sealed class ManualHandoffViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// Plain, factual, and shown before anything is copied. Not legal prose — a clear statement of
    /// where the text goes and what is not in it.
    /// </summary>
    public const string PrivacyNotice =
        "This copies the meeting text below to your clipboard so you can paste it into ChatGPT, Claude " +
        "or another assistant yourself. EchoForge does not send anything. Once you paste it elsewhere, " +
        "that service's privacy and retention policy applies. No audio and no files are included — only " +
        "the text shown here.";

    private readonly Func<ManualHandoffOptions, ManualHandoffResult> _compose;
    private readonly IClipboardWriter _clipboard;

    private string _previewText = string.Empty;
    private string _scopeText = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _includeSummaryReference;
    private bool _available;

    public ManualHandoffViewModel(
        Func<ManualHandoffOptions, ManualHandoffResult> compose,
        IClipboardWriter clipboard,
        string suggestedFileName)
    {
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "handoff.md" : suggestedFileName;

        Recompose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SuggestedFileName { get; }

    /// <summary>The exact text that will be copied or saved. Editable; edits are the redaction.</summary>
    public string PreviewText
    {
        get => _previewText;
        set
        {
            if (string.Equals(_previewText, value, StringComparison.Ordinal))
            {
                return;
            }

            _previewText = value ?? string.Empty;
            Changed();
            Changed(nameof(CharacterCount));
            Changed(nameof(ApproximateTokenCount));
            Changed(nameof(CountsText));
            Changed(nameof(CanCopy));
        }
    }

    /// <summary>Whether EchoForge's local summary is appended as reference. Off by default.</summary>
    public bool IncludeSummaryReference
    {
        get => _includeSummaryReference;
        set
        {
            if (_includeSummaryReference == value)
            {
                return;
            }

            _includeSummaryReference = value;
            Changed();
            // A structural change regenerates the preview; any manual edits are replaced.
            Recompose();
        }
    }

    public bool IsAvailable
    {
        get => _available;
        private set { _available = value; Changed(); Changed(nameof(CanCopy)); }
    }

    public bool CanCopy => IsAvailable && PreviewText.Length > 0;

    public int CharacterCount => PreviewText.Length;

    public int ApproximateTokenCount => ManualHandoffPayload.ApproximateTokens(PreviewText);

    public string CountsText => string.Create(
        CultureInfo.CurrentCulture,
        $"{CharacterCount:N0} characters · about {ApproximateTokenCount:N0} tokens (approximate)");

    public string ScopeText
    {
        get => _scopeText;
        private set { _scopeText = value; Changed(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; Changed(); Changed(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Copies the current preview. The only path to the clipboard, and only on demand.</summary>
    public ClipboardResult Copy()
    {
        if (!CanCopy)
        {
            ClipboardResult empty = ClipboardResult.Fail("There is nothing to copy.");
            StatusMessage = empty.Message;
            return empty;
        }

        ClipboardResult result = _clipboard.TrySetText(PreviewText);
        StatusMessage = result.Message;
        return result;
    }

    /// <summary>Saves the current preview to a file the user chose.</summary>
    public ExportResult Save(string destinationPath, bool overwrite = false)
    {
        if (!IsAvailable || PreviewText.Length == 0)
        {
            ExportResult empty = ExportResult.Fail("empty", "There is nothing to save.");
            StatusMessage = empty.Message;
            return empty;
        }

        ExportResult result = ManualHandoffWriter.Save(PreviewText, destinationPath, overwrite);
        StatusMessage = result.Succeeded ? "Saved to " + result.Path : result.Message;
        return result;
    }

    private void Recompose()
    {
        ManualHandoffResult result = _compose(new ManualHandoffOptions
        {
            IncludeSummaryReference = _includeSummaryReference,
        });

        if (!result.Succeeded || result.Payload is not { } payload)
        {
            IsAvailable = false;
            PreviewText = string.Empty;
            ScopeText = string.Empty;
            StatusMessage = result.Message;
            return;
        }

        IsAvailable = true;
        PreviewText = payload.Text;
        ScopeText = payload.IsSubset
            ? string.Create(CultureInfo.CurrentCulture, $"Selected subset: {payload.IncludedSegmentCount} of {payload.TotalSegmentCount} segments · revision {payload.TranscriptRevision} · {payload.TemplateVersion}")
            : string.Create(CultureInfo.CurrentCulture, $"Whole transcript: {payload.IncludedSegmentCount} segments · revision {payload.TranscriptRevision} · {payload.TemplateVersion}");
        StatusMessage = string.Empty;
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
