using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Buzzr.Services;
using static Buzzr.Theme.T;

namespace Buzzr.Views;

public sealed partial class NewChatDialog : ContentDialog
{
    private List<BeeperAccount> _accounts;
    private string? _selectedAccountId;
    private string? _selectedContactId;
    private System.Threading.CancellationTokenSource? _searchCts;

    public BeeperChat? SelectedChat { get; private set; }

    public NewChatDialog(List<BeeperAccount> accounts)
    {
        this.InitializeComponent();
        _accounts = accounts;
        this.IsPrimaryButtonEnabled = false;

        // Populate accounts
        foreach (var a in _accounts)
        {
            var name = a.Network ?? a.AccountId;
            if (a.User?.FullName != null) name += $" ({a.User.FullName})";
            AccountPicker.Items.Add(new ComboBoxItem { Content = name, Tag = a.AccountId });
        }

        ContactSearch.TextChanged += (s, e) => _ = OnContactSearchChangedAsync();
        ContactList.SelectionChanged += OnContactSelected;
        this.PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private async System.Threading.Tasks.Task OnContactSearchChangedAsync()
    {
        if (AccountPicker.SelectedItem is not ComboBoxItem item) return;
        var accountId = item.Tag as string;
        if (string.IsNullOrEmpty(accountId)) return;
        _selectedAccountId = accountId;

        var query = ContactSearch.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ContactList.Items.Clear();
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new System.Threading.CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await System.Threading.Tasks.Task.Delay(300, token);
            if (token.IsCancellationRequested) return;

            SearchProgress.IsActive = true;
            SearchProgress.Visibility = Visibility.Visible;

            var result = await App.Api.SearchContactsAsync(accountId, query);
            var contactList = result?.Items ?? new();

            if (token.IsCancellationRequested) return;

            SearchProgress.IsActive = false;
            SearchProgress.Visibility = Visibility.Collapsed;

            ContactList.Items.Clear();
            foreach (var c in contactList)
            {
                var display = c.FullName ?? c.Username ?? c.PhoneNumber ?? c.Id;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

                // Avatar
                if (!string.IsNullOrEmpty(c.ImgUrl))
                {
                    try
                    {
                        var bmp = new BitmapImage(new Uri(c.ImgUrl)) { DecodePixelWidth = 28, DecodePixelHeight = 28 };
                        var img = new Image { Source = bmp, Width = 28, Height = 28, Stretch = Stretch.UniformToFill };
                        row.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Child = img, VerticalAlignment = VerticalAlignment.Center });
                    }
                    catch
                    {
                        row.Children.Add(Avatar(display, 28));
                    }
                }
                else
                {
                    row.Children.Add(Avatar(display, 28));
                }

                var nameBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
                nameBlock.Children.Add(Lbl(display, 13, Fg1));
                if (c.PhoneNumber != null && c.FullName != null)
                    nameBlock.Children.Add(Lbl(c.PhoneNumber, 11, Fg3));
                row.Children.Add(nameBlock);

                ContactList.Items.Add(new ListViewItem { Content = row, Tag = c.Id });
            }

            if (contactList.Count == 0)
            {
                ContactList.Items.Add(new ListViewItem
                {
                    Content = Lbl("No contacts found", 12, Fg3),
                    IsEnabled = false
                });
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            SearchProgress.IsActive = false;
            SearchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnContactSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ContactList.SelectedItem is ListViewItem item && item.Tag is string contactId)
        {
            _selectedContactId = contactId;
            this.IsPrimaryButtonEnabled = !string.IsNullOrEmpty(_selectedContactId);
        }
        else
        {
            _selectedContactId = null;
            this.IsPrimaryButtonEnabled = false;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The caller will use SelectedChat to navigate
        // For now we create a stub chat that can be used
        if (!string.IsNullOrEmpty(_selectedAccountId) && !string.IsNullOrEmpty(_selectedContactId))
        {
            SelectedChat = new BeeperChat
            {
                Id = _selectedContactId,
                AccountId = _selectedAccountId,
                Title = _selectedContactId,
                Type = "single"
            };

            // If there's a first message, send it
            var msgText = FirstMessage.Text?.Trim();
            if (!string.IsNullOrEmpty(msgText))
            {
                _ = App.Api.SendMessageAsync(_selectedContactId, msgText);
            }
        }
    }

    public string? SelectedAccountId => _selectedAccountId;
    public string? SelectedContactId => _selectedContactId;
    public string? MessageText => string.IsNullOrWhiteSpace(FirstMessage.Text) ? null : FirstMessage.Text;
}
