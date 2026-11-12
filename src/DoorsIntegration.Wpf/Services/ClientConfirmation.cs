using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Wpf.Services
{
    public interface IClientReview
    {
        Task<bool> ReviewAsync(WriteReview review, CancellationToken token);
    }

    // The service asks again at commit, including every native batch record and separate removal approval.
    public sealed class ClientConfirmation : IClientReview, IRequirementWriteConfirmation, IModuleCreationConfirmation, ILinkWriteConfirmation, IBatchConfirmation
    {
        private readonly IClientReview dialog;
        public ClientConfirmation(IClientReview dialog) { this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog)); }
        public Task<bool> ReviewAsync(WriteReview review, CancellationToken token) => dialog.ReviewAsync(review, token);
        public Task<bool> ConfirmAsync(RequirementWritePreview preview, CancellationToken token) => ReviewAsync(WriteReview.From(preview), token);
        public Task<bool> ConfirmAsync(FormalModuleCreationPreview preview, CancellationToken token) => ReviewAsync(WriteReview.From(preview), token);
        public Task<bool> ConfirmAsync(LinkWritePreview preview, CancellationToken token) => ReviewAsync(WriteReview.From(preview), token);
        public Task<bool> ConfirmRemovalAsync(LinkWritePreview preview, CancellationToken token) => ReviewAsync(WriteReview.From(preview, true), token);
        public Task<bool> ConfirmAsync(BatchPreview preview, CancellationToken token) => ReviewAsync(new WriteReview("Toplu işlem onayı",
            preview.Options.OperationNamespace, "Her kayıt için ayrıca güncel native önizleme ve açık onay istenir. İşlem atomik değildir.\n" +
            "Repository=" + preview.Options.RepositoryId + "; Namespace=" + preview.Options.OperationNamespace + "\nRows=" + preview.RecordCount +
            "; Ready=" + preview.ReadyCount + "; Blocked=" + preview.BlockedCount + "; ContinueOnError=" + preview.Options.ContinueOnError +
            "; LifetimeAttempts=" + preview.Options.MaxAttempts + "\nCaptured preview=" + preview.FilePath + "\nFingerprint=" + preview.Fingerprint +
            "\nİlk 100 kayıt (tüm kayıtları önizleme dosyasında inceleyin):\n" + string.Join("\n", preview.ReadRows().Take(100).Select(r =>
                r.RecordNumber + ": " + r.Change?.Action + " " + r.Change?.Target + " #" + r.Change?.ObjectId + "; " + r.Status + "; " + r.Code)) +
            "\n" + ProcessingDisclosure.Statement, preview.ExpiresAtUtc, preview.CanApply), token);
    }

    public sealed class WpfReviewDialog : IClientReview
    {
        private readonly Func<Window> owner;
        private readonly Func<bool> allowed;
        private readonly string acknowledgement;
        private readonly string applyLabel;
        public WpfReviewDialog(Func<Window> owner, Func<bool> allowed, string acknowledgement = "Bu değişikliği / script yürütmesini açıkça onaylıyorum.", string applyLabel = "Onayla ve uygula")
        { this.owner = owner; this.allowed = allowed; this.acknowledgement = acknowledgement; this.applyLabel = applyLabel; }
        public async Task<bool> ReviewAsync(WriteReview review, CancellationToken token)
        {
            var app = Application.Current;
            if (app == null || token.IsCancellationRequested) return false;
            return await app.Dispatcher.InvokeAsync(() => Show(review, token));
        }
        private bool Show(WriteReview review, CancellationToken token)
        {
            var parent = owner();
            if (parent == null || !parent.IsVisible || !allowed() || !review.CanApply || token.IsCancellationRequested || review.ExpiresAtUtc <= DateTime.UtcNow) return false;
            var window = new Window { Owner = parent, Title = review.Title, Width = 940, Height = 760, MinWidth = 680, MinHeight = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Style = (Style)parent.FindResource(typeof(Window)) };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
            footer.Children.Add(new TextBlock { Text = "Onaylamak için hedefi aynen yazın: " + review.Target, TextWrapping = TextWrapping.Wrap });
            var typed = new TextBox(); footer.Children.Add(typed);
            var acknowledge = new CheckBox { Content = acknowledgement, Margin = new Thickness(4, 10, 4, 10) }; footer.Children.Add(acknowledge);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var apply = new Button { Content = applyLabel, IsEnabled = false };
            var cancel = new Button { Content = "Vazgeç", IsCancel = true };
            buttons.Children.Add(apply); buttons.Children.Add(cancel); footer.Children.Add(buttons);
            panel.Children.Add(new TextBox { Text = review.Body, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            window.Content = panel;
            Func<bool> valid = () => allowed() && review.CanApply && typed.Text == review.Target && acknowledge.IsChecked == true &&
                !token.IsCancellationRequested && DateTime.UtcNow < review.ExpiresAtUtc && parent.IsVisible;
            Action refresh = () => apply.IsEnabled = valid();
            typed.TextChanged += (s, e) => refresh(); acknowledge.Checked += (s, e) => refresh(); acknowledge.Unchecked += (s, e) => refresh();
            apply.Click += (s, e) => { if (valid()) window.DialogResult = true; };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (s, e) => { refresh(); if (token.IsCancellationRequested || DateTime.UtcNow >= review.ExpiresAtUtc || !allowed()) window.Close(); };
            timer.Start();
            try { return window.ShowDialog() == true && valid(); }
            finally { timer.Stop(); }
        }
    }
}
