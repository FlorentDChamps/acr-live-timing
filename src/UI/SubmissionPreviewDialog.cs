using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ACRLiveTiming.Submission;

namespace ACRLiveTiming.UI;

/// <summary>
/// The confirmation shown before results leave the board: what will be sent or
/// saved, to whom, the warnings, and what blocks. Nothing happens before
/// <c>Confirm</c>; while anything blocks, Confirm is disabled.
/// </summary>
public sealed class SubmissionPreviewDialog : Window
{
    private static readonly string[] ThemeKeys =
    [
        "Brush.Window", "Brush.Card", "Brush.Input", "Brush.Border", "Brush.BorderHover", "Brush.WindowOutline",
        "Brush.Text", "Brush.ButtonText", "Brush.ButtonBg", "Brush.ButtonHover",
        "Brush.ButtonPressed", "Brush.Muted", "Brush.Accent", "Brush.Danger"
    ];

    private bool _confirmed;

    private SubmissionPreviewDialog(Window owner, string selection, ResultsSnapshot snapshot, IReadOnlyList<Association> destinations)
    {
        Owner = owner;
        CopyTheme(owner);
        bool save = destinations.Count == 0;
        Title = "ACR Live Timing";
        Width = 560;
        // NoResize: a resizable borderless window gets a white resize frame
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 80);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var frame = new Border { BorderThickness = new Thickness(1), Child = root };
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.WindowOutline");
        Content = frame;

        var titleBar = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        titleBar.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        titleBar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        titleBar.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* released before the drag began */ } };
        root.Children.Add(titleBar);
        var titleLayout = new Grid();
        titleBar.Child = titleLayout;
        titleLayout.Children.Add(new TextBlock
        {
            Text = save ? "Save results" : "Send results",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        });
        var close = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 44,
            Height = 35,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = owner.TryFindResource("CloseCaptionButton") as Style,
            ToolTip = "Cancel"
        };
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.Click += (_, _) => Close();
        titleLayout.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(layout, 1);
        root.Children.Add(layout);

        var body = new StackPanel();
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        layout.Children.Add(scroll);

        Heading(body, selection);
        Line(body, $"{snapshot.Stages.Count} stage(s), {snapshot.Drivers.Count} driver(s).", muted: true);
        foreach (var stage in snapshot.Stages) Line(body, "• " + stage.Label, muted: true);

        bool blocked = snapshot.Problems.Count > 0;
        if (blocked)
        {
            Heading(body, "Cannot be sent yet");
            foreach (string problem in snapshot.Problems) Line(body, "✕ " + problem, danger: true);
            Line(body, "Wait for the game's session result, or leave the stage out: untick it for All stages, ungroup it for a rally.", muted: true);
        }

        var warnings = new List<string>(snapshot.Warnings);
        foreach (var destination in destinations)
        {
            var late = snapshot.Stages.Where(s => !destination.StartedAfterPairing(s.StartTimestamp)).ToList();
            if (late.Count > 0)
                warnings.Add($"{destination.Host}: {late.Count} stage(s) started before pairing ({string.Join(", ", late.Select(s => s.Label))}).");
        }
        if (warnings.Count > 0)
        {
            Heading(body, "Warnings");
            foreach (string warning in warnings) Line(body, "⚠ " + warning);
        }

        int ids = snapshot.Drivers.Count(d => d.SteamId64 != null || d.EosPuid != null);
        Heading(body, save ? "File" : "Destinations");
        if (save)
        {
            Line(body, $"{ids} driver(s): their SteamID/EOS IDs will be written in clear to the file you choose.");
        }
        else
        {
            foreach (var destination in destinations)
            {
                Line(body, destination.Label);
                Line(body, $"{ids} driver(s): their SteamID/EOS IDs will be sent to {destination.Host}.", muted: true);
            }
            Line(body, $"A copy of each send is saved in {ResultFiles.Folder}.", muted: true);
            ((FrameworkElement)body.Children[^1]).Margin = new Thickness(0, 10, 0, 1);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var confirm = new Button { Content = save ? "Save…" : "Send", IsEnabled = !blocked, IsDefault = !blocked };
        confirm.Click += (_, _) => { _confirmed = true; Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>True when the operator confirmed.</summary>
    public static bool Show(Window owner, string selection, ResultsSnapshot snapshot, IReadOnlyList<Association> destinations)
    {
        var dialog = new SubmissionPreviewDialog(owner, selection, snapshot, destinations);
        dialog.ShowDialog();
        return dialog._confirmed;
    }

    private static void Heading(Panel panel, string text)
        => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 12, 0, 4), TextWrapping = TextWrapping.Wrap });

    private static void Line(Panel panel, string text, bool muted = false, bool danger = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        if (muted) block.SetResourceReference(ForegroundProperty, "Brush.Muted");
        if (danger) block.SetResourceReference(ForegroundProperty, "Brush.Danger");
        panel.Children.Add(block);
    }

    private void CopyTheme(Window owner)
    {
        foreach (string key in ThemeKeys)
            if (owner.TryFindResource(key) is object value) Resources[key] = value;
        if (owner.TryFindResource(typeof(Button)) is Style buttonStyle) Resources[typeof(Button)] = buttonStyle;
        if (owner.TryFindResource(typeof(ScrollBar)) is Style scrollStyle) Resources[typeof(ScrollBar)] = scrollStyle;
        if (owner.TryFindResource("CloseCaptionButton") is Style closeStyle) Resources["CloseCaptionButton"] = closeStyle;
    }
}
