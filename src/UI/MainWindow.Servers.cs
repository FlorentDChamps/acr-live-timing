using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ACRLiveTiming.Model;
using ACRLiveTiming.Submission;
using Microsoft.Win32;

namespace ACRLiveTiming.UI;

// Results submission: the Servers panel (pairing, associations, Send/Save).
public partial class MainWindow
{
    private readonly AssociationStore _associations = new();
    // what the page shows of the active associations, rebuilt on the UI thread and
    // read by the /state serializer on the HTTP thread
    private volatile List<EventView> _publicEvents = [];
    private bool _pairing;
    private sealed record SelectionOption(int Group, string Name);

    private void InitServers()
    {
        _associations.Load();
        _associations.Changed += RefreshServers;
        _associations.ExpireTokens(DateTimeOffset.UtcNow);
        RefreshServers();
    }

    private void RefreshServers()
    {
        if (ServersPanel == null) return;
        // the page shows the events results are sent to: active and not paused
        _publicEvents = [.. _associations.Active.Where(a => a.Selected).Select(a => new EventView(a.PublicServer, a.PublicEvent))];
        _stateJson = null;

        ServersPanel.Children.Clear();
        foreach (var association in _associations.Items)
            ServersPanel.Children.Add(ServerRow(association));
        ServersEmptyText.Visibility = _associations.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool any = _associations.Items.Any(a => a.Selected && a.HasToken && !a.Sending);
        ServersSendBtn.Content = any ? "Send…" : "Save…";
        ServersSendBtn.IsEnabled = ServersSelectionBox.SelectedItem is SelectionOption
                                   && !_associations.Items.Any(a => a.Selected && a.Sending);
        RefreshSettingWarnings();
    }

    // Segoe MDL2 glyphs of the association states
    private const string GlyphPlay = "\uE768", GlyphPause = "\uE769", GlyphStop = "\uE71A",
                         GlyphBlocked = "\uE711", GlyphDelete = "\uE74D";

    /// <summary>One association: its state button (▶ sent to, ⏸ paused, ⏹ ended,
    /// blocked once the token is gone), its label and a delete button on the first
    /// line, its state and last answer on a second line running under the delete
    /// button. The state button switches between sending to it or not.</summary>
    private FrameworkElement ServerRow(Association association)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        bool ended = association.State == AssociationState.Ended;
        (string glyph, string brush, string state, string tip) = !association.HasToken
            ? (GlyphBlocked, "Brush.Danger",
               association.State == AssociationState.Expired ? "Expired — a new key is needed" : "Refused — a new key is needed",
               "This association has no token any more.")
            : association.Selected
                ? (GlyphPlay, "Brush.Accent", ended ? "Ended — corrections only" : "Active",
                   "Send… sends to this server" + (ended ? "." : "; the web page shows its event.") + " Click to pause.")
                : ended
                    ? (GlyphStop, "Brush.Muted", "Ended", "The server ended this event. Click to send it a correction.")
                    : (GlyphPause, "Brush.Muted", "Paused", "Not sent to, and not shown on the web page. Click to resume.");
        if (association.Sending) state = "Sending…";

        var toggle = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Padding = new Thickness(7, 5, 7, 5),
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = association.HasToken && !association.Sending,
            ToolTip = tip
        };
        toggle.SetResourceReference(Control.ForegroundProperty, brush);
        toggle.Click += (_, _) => { association.Selected = !association.Selected; RefreshServers(); };
        row.Children.Add(toggle);

        var label = new TextBlock
        {
            Text = association.Label,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = association.Selected && association.HasToken ? FontWeights.SemiBold : FontWeights.Normal,
            ToolTip = (association.AuthorizationId is string auth ? $"Authorization: {TextSanitizer.Clean(auth, 80)}" : "Event: " + TextSanitizer.Clean(association.EventId, 128))
                      + (association.ExpiresAt is DateTimeOffset exp ? $"\nExpires: {exp.ToLocalTime():yyyy-MM-dd HH:mm}" : "")
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var status = new TextBlock
        {
            // without a token the state says it all; the last answer stays in the tooltip
            Text = association.LastSend.Length > 0 && association.HasToken && !association.Sending
                ? state + " · " + association.LastSend : state,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 1, 0, 0),
            ToolTip = association.LastSendDetail.Length > 0 ? association.LastSendDetail
                : association.LastSend.Length > 0 ? association.LastSend : null
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, association.HasToken ? "Brush.Muted" : "Brush.Danger");
        Grid.SetRow(status, 1);
        Grid.SetColumn(status, 1);
        Grid.SetColumnSpan(status, 2);
        row.Children.Add(status);

        var remove = new Button
        {
            Content = GlyphDelete,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Padding = new Thickness(7, 5, 7, 5),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = association.HasToken ? "Remove: delete this association and its token. A new key is then needed." : "Remove from the list."
        };
        remove.Click += (_, _) => RemoveAssociation(association);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }

    private void RefreshServerSelections(MatrixView view)
    {
        if (ServersSelectionBox == null) return;
        int? selected = (ServersSelectionBox.SelectedItem as SelectionOption)?.Group;
        var options = view.AllStages.Where(stage => stage.Group > 0)
            .GroupBy(stage => stage.Group)
            .OrderBy(group => group.Key)
            .Select(group => new SelectionOption(group.Key,
                string.IsNullOrWhiteSpace(group.First().GroupName) ? $"Rally {group.Key}" : group.First().GroupName))
            .ToList();
        if (view.AllStages.Count > 0) options.Insert(0, new SelectionOption(0, "All stages"));
        ServersSelectionBox.ItemsSource = options;
        ServersSelectionBox.SelectedItem = options.FirstOrDefault(o => o.Group == selected) ?? options.FirstOrDefault();
        ServersSelectionBox.IsEnabled = options.Count > 0;
        RefreshServers();
    }

    private void ServersSelectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshServers();

    // ---- pairing ----------------------------------------------------------

    private void PairKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        string text = PairKeyBox.Password.Trim();
        PairKeyPlaceholder.Visibility = PairKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length == 0)
        {
            PairKeyInfo.Visibility = Visibility.Collapsed;
            PairBtn.IsEnabled = false;
            return;
        }
        bool ok = PairingKey.TryParse(text, out var key, out string error);
        PairKeyInfo.Text = ok ? "Server: " + key!.Host : error;
        PairKeyInfo.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Brush.Text" : "Brush.Danger");
        PairKeyInfo.Visibility = Visibility.Visible;
        PairBtn.IsEnabled = ok && !_pairing;
    }

    private async void PairBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!PairingKey.TryParse(PairKeyBox.Password, out var key, out string error))
        {
            AppendLog("Pairing: " + error);
            return;
        }
        string hash = key!.Hash;
        var known = _associations.Items.FirstOrDefault(a => a.KeyHash == hash && a.HasToken);
        if (known != null)
        {
            // a key already exchanged reuses its association without a request
            PairKeyBox.Password = "";
            AppendLog("Already paired: " + known.Label);
            return;
        }

        _pairing = true;
        PairBtn.IsEnabled = false;
        PairKeyInfo.Text = "Pairing with " + key.Host + "…";
        ExchangeResult result;
        try { result = await SubmissionClient.ExchangeAsync(key, _associations.ClientInstanceId); }
        finally { _pairing = false; }

        if (result.Association is Association association)
        {
            association.SetToken(result.Token!);
            _associations.Items.RemoveAll(a => a.KeyHash == hash && !a.HasToken);   // the refused entry it replaces
            _associations.Items.Add(association);
            _associations.Save();
            PairKeyBox.Password = "";
            AppendLog("Paired: " + association.Label + ".");
            if (result.IgnoredSettings is { Count: > 0 } ignored)
                AppendLog($"Invalid event setting(s) from {association.PublicServer} ignored: {string.Join(", ", ignored)}.");
            ApplyEventSettings(association);
            return;
        }

        bool kept = _associations.Items.Any(a => a.HasToken && ServerAddress.SameOrigin(a.Server, key.ServerUrl));
        string message = result.Message + (kept ? " Existing association kept." : "");
        PairKeyInfo.Text = message;
        PairKeyInfo.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        PairKeyInfo.Visibility = Visibility.Visible;
        PairBtn.IsEnabled = result.KeepKey;
        AppendLog($"Pairing with {key.Host} failed: {message}");
    }

    private void ApplyEventSettings(Association association)
    {
        var settings = association.EventSettings;
        if (settings == null) return;
        string who = association.PublicServer;
        if (settings.PenaltyCapPct is double pct)
        {
            PctBox.Text = pct.ToString(CultureInfo.InvariantCulture);
            AppendLog($"Penalty cap set to {pct.ToString(CultureInfo.InvariantCulture)} % by {who}.");
        }
        if (settings.DnfNoRejoin is bool dnf)
        {
            DnfNoRejoinCheck.IsChecked = dnf;
            AppendLog($"DNF – No Rejoin turned {(dnf ? "on" : "off")} by {who}.");
        }
        SaveSettings();
        RefreshSettingWarnings();
    }

    /// <summary>A small warning next to a board setting that differs from what an
    /// active association suggested.</summary>
    private void RefreshSettingWarnings()
    {
        if (PctWarn == null || DnfWarn == null) return;
        double pct = ParseD(PctBox.Text, double.NaN);
        bool dnf = DnfNoRejoinCheck.IsChecked == true;
        var pctOff = _associations.Active
            .Where(a => a.EventSettings?.PenaltyCapPct is double v && Math.Abs(v - pct) > 1e-9).ToList();
        var dnfOff = _associations.Active
            .Where(a => a.EventSettings?.DnfNoRejoin is bool v && v != dnf).ToList();
        PctWarn.Visibility = pctOff.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PctWarn.ToolTip = string.Join("\n", pctOff.Select(a =>
            $"{a.PublicServer} — {a.PublicEvent} suggests {a.EventSettings!.PenaltyCapPct!.Value.ToString(CultureInfo.InvariantCulture)} %."));
        DnfWarn.Visibility = dnfOff.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DnfWarn.ToolTip = string.Join("\n", dnfOff.Select(a =>
            $"{a.PublicServer} — {a.PublicEvent} suggests DNF – No Rejoin {(a.EventSettings!.DnfNoRejoin == true ? "on" : "off")}."));
    }

    private void RemoveAssociation(Association association)
    {
        if (association.HasToken)
        {
            var confirm = MessageBox.Show(this,
                $"Remove {association.Label}?\n\nIts token is deleted: sending to this event again needs a new key.",
                "Remove association?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }
        association.DeleteToken(AssociationState.Ended);
        _associations.Items.Remove(association);
        _associations.Save();
        AppendLog("Removed: " + association.Label + ".");
    }

    /// <summary>Default name of a group created while an association is active.</summary>
    private string? ActiveEventName()
        => _associations.Active.Select(a => a.EventName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) is string n
            ? TextSanitizer.Clean(n, 200) : null;

    // ---- send / save ------------------------------------------------------

    private async void ServersSendBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ServersSelectionBox.SelectedItem is not SelectionOption selection) return;
        var destinations = _associations.Items.Where(a => a.Selected && a.HasToken && !a.Sending).ToList();
        var snapshot = _engine.Matrix.BuildSubmission(selection.Group);
        if (!SubmissionPreviewDialog.Show(this, selection.Name, snapshot, destinations)) return;
        DateTime generated = DateTime.UtcNow;

        if (destinations.Count == 0)
        {
            SaveResultsFile(selection, snapshot, generated);
            return;
        }

        await Task.WhenAll(destinations.Select(d => SendAsync(d, selection, snapshot, generated)));
    }

    private void SaveResultsFile(SelectionOption selection, ResultsSnapshot snapshot, DateTime generated)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save results",
            Filter = "JSON results (*.json)|*.json",
            FileName = $"{DateTime.Now:yyyyMMdd-HHmmss}_{ResultFiles.Safe(selection.Name)}.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, ResultsPayload.ForFile(snapshot, generated));
            AppendLog($"Results saved: {dialog.FileName}");
        }
        catch (Exception ex) { AppendLog("Results not saved: " + ex.Message); }
    }

    private async Task SendAsync(Association destination, SelectionOption selection, ResultsSnapshot snapshot, DateTime generated)
    {
        byte[] body = ResultsPayload.ForDestination(snapshot, destination, generated);
        try { ResultFiles.SaveCopy(body, destination, generated.ToLocalTime()); }
        catch (Exception ex) { AppendLog($"Copy of the send to {destination.Host} not saved: {ex.Message}"); }

        destination.Sending = true;
        RefreshServers();
        SubmitResult result;
        try { result = await SubmissionClient.SubmitAsync(destination, body); }
        finally { destination.Sending = false; }

        destination.LastSend = $"{DateTime.Now:HH:mm} — {selection.Name} — {result.Message}";
        destination.LastSendDetail = result.Status is int status ? $"HTTP {status}: {result.Message}" : result.Message;
        if (result.TokenRefused)
        {
            destination.DeleteToken(AssociationState.Refused);
        }
        else if (result.Stored && result.Active is bool active)
        {
            destination.State = active ? AssociationState.Active : AssociationState.Ended;
            if (!active) destination.Selected = false;
        }
        _associations.Save();
        AppendLog($"Results ({selection.Name}) to {destination.Host}: {result.Message}");
    }

    private void OpenResultsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ResultFiles.Folder);
            Process.Start(new ProcessStartInfo { FileName = ResultFiles.Folder, UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog("Open folder error: " + ex.Message); }
    }

}
