using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ACRLiveTiming.Updates;

namespace ACRLiveTiming.UI
{
    public enum UpdateDialogResult { Later, Download, Discard }

    /// <summary>Update prompt using the same palette and custom title bar as the
    /// main window. Closing it with the cross means "Later"; only Discard is saved.</summary>
    public sealed class UpdateAvailableDialog : Window
    {
        static readonly string[] ThemeKeys =
        {
            "Brush.Window", "Brush.Card", "Brush.Input", "Brush.Border", "Brush.BorderHover", "Brush.WindowOutline",
            "Brush.Text", "Brush.ButtonText", "Brush.ButtonBg", "Brush.ButtonHover",
            "Brush.ButtonPressed", "Brush.Muted", "Brush.Accent", "Brush.Danger"
        };

        public UpdateDialogResult Result { get; private set; } = UpdateDialogResult.Later;

        UpdateAvailableDialog(Window owner, AvailableUpdate update)
        {
            Owner = owner;
            CopyTheme(owner);
            Title = "ACR Live Timing";
            Width = 450;
            Height = 218;
            MinWidth = Width;
            MinHeight = Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
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
            titleBar.MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
            root.Children.Add(titleBar);

            var titleLayout = new Grid();
            titleBar.Child = titleLayout;
            var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            identity.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/assets/icon.ico")), Width = 20, Height = 20 });
            identity.Children.Add(new TextBlock { Text = "Update available", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            titleLayout.Children.Add(identity);
            var close = new Button
            {
                Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 44, Height = 35, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = owner.TryFindResource("CloseCaptionButton") as Style,
                ToolTip = "Close"
            };
            close.MouseLeftButtonDown += (_, e) => e.Handled = true;
            close.Click += (_, _) => Close();
            titleLayout.Children.Add(close);

            var layout = new Grid { Margin = new Thickness(18, 16, 18, 14) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(layout, 1);
            root.Children.Add(layout);

            layout.Children.Add(new TextBlock { Text = $"Version {update.Version} is available", FontWeight = FontWeights.SemiBold, FontSize = 15 });
            var explanation = new TextBlock
            {
                Text = "Download the new Windows executable, or view the release notes and SHA-256 checksum first.",
                Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap
            };
            explanation.SetResourceReference(ForegroundProperty, "Brush.Muted");
            Grid.SetRow(explanation, 1);
            layout.Children.Add(explanation);

            var releaseLink = new TextBlock { Margin = new Thickness(0, 9, 0, 0) };
            var link = new Hyperlink(new Run("View release notes and checksum")) { Cursor = Cursors.Hand };
            link.SetResourceReference(Hyperlink.ForegroundProperty, "Brush.Accent");
            link.Click += (_, _) => OpenUrl(update.ReleaseUrl);
            releaseLink.Inlines.Add(link);
            Grid.SetRow(releaseLink, 2);
            layout.Children.Add(releaseLink);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(Button(owner, "Discard", (_, _) => { Result = UpdateDialogResult.Discard; Close(); }, new Thickness(0, 0, 8, 0)));
            buttons.Children.Add(Button(owner, "Download EXE", (_, _) => { Result = UpdateDialogResult.Download; Close(); }, new Thickness()));
            Grid.SetRow(buttons, 3);
            layout.Children.Add(buttons);
        }

        public static UpdateDialogResult Show(Window owner, AvailableUpdate update)
        {
            var dialog = new UpdateAvailableDialog(owner, update);
            dialog.ShowDialog();
            return dialog.Result;
        }

        void CopyTheme(Window owner)
        {
            foreach (var key in ThemeKeys)
                if (owner.TryFindResource(key) is object value) Resources[key] = value;
            if (owner.TryFindResource(typeof(Button)) is Style buttonStyle) Resources[typeof(Button)] = buttonStyle;
            if (owner.TryFindResource("CloseCaptionButton") is Style closeStyle) Resources["CloseCaptionButton"] = closeStyle;
        }

        static Button Button(Window owner, string text, RoutedEventHandler click, Thickness margin)
        {
            var button = new Button { Content = text, Margin = margin };
            button.Click += click;
            return button;
        }

        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); }
            catch { /* mouse released before the drag began */ }
        }

        static void OpenUrl(Uri url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url.AbsoluteUri, UseShellExecute = true }); }
            catch { /* clicking the link is optional; the update dialog remains open */ }
        }
    }
}
