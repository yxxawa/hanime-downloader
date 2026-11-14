using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;

internal static partial class Program
{
    private static T Find<T>(SettingsDialog dialog, string name) where T : FrameworkElement =>
        (T)dialog.FindName(name);

    private static void ClickSetting(SettingsDialog dialog, string method) =>
        typeof(SettingsDialog).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, [dialog, new RoutedEventArgs()]);

    private static void CheckSettingsWorkflow(Application app)
    {
        var original = new AppSettings
        {
            DownloadPath = @"C:\Videos\Downloads", SiteHost = "hanime1.me", FavoritesMode = AppSettings.AccountFavoritesMode,
            AccountEmail = "old@example.invalid", AccountUserId = "17", AccountUserName = "原账号",
            CustomSiteHosts = ["saved.example.invalid"], ShowListCovers = false, LargeSearchResultCovers = true
        };
        var bindingCalls = 0;
        var dialog = new SettingsDialog(original, (email, password) =>
        {
            bindingCalls++;
            Check(email == "new@example.invalid" && password == "synthetic", "binding receives the entered draft credentials");
            return Task.FromResult(new HanimeAccountIdentity(UserId: "18", UserName: "新账号", Email: email));
        });
        var pages = Find<TabControl>(dialog, "SettingsPages");
        Check(pages.Items.Count == 5 && pages.TabStripPlacement == Dock.Left, "settings has five categories in a left sidebar");
        var downloadPage = Find<TabItem>(dialog, "DownloadPage");
        var sitePage = Find<TabItem>(dialog, "SitePage");
        var namingPage = Find<TabItem>(dialog, "NamingPage");
        var appearancePage = Find<TabItem>(dialog, "AppearancePage");
        var content = (FrameworkElement)dialog.Content;
        pages.SelectedItem = downloadPage;
        Capture(content, "artifacts/settings-download-light.png", 808, 626);
        pages.SelectedItem = namingPage;
        Capture(content, "artifacts/settings-naming-light.png", 808, 626);
        pages.SelectedItem = sitePage;
        Capture(content, "artifacts/settings-site-light.png", 808, 626);
        var light = app.Resources.MergedDictionaries[0];
        app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("/Hanime1Downloader.CSharp;component/Themes/DarkTheme.xaml", UriKind.Relative) };
        Capture(content, "artifacts/settings-site-dark.png", 808, 626);
        pages.SelectedItem = appearancePage;
        Capture(content, "artifacts/settings-appearance-dark-min.png", 646, 484);
        app.Resources.MergedDictionaries[0] = light;

        var form = Find<Grid>(dialog, "AccountForm");
        var identity = Find<Grid>(dialog, "BoundAccountPanel");
        Check(form.Visibility == Visibility.Collapsed && identity.Visibility == Visibility.Visible,
            "a bound account shows its identity without showing credential inputs");
        pages.SelectedItem = sitePage;
        ClickSetting(dialog, "EditAccount_OnClick");
        var email = Find<TextBox>(dialog, "AccountEmailBox");
        var password = Find<PasswordBox>(dialog, "AccountPasswordBox");
        email.Text = "discard@example.invalid";
        password.Password = "discard";
        ClickSetting(dialog, "CancelAccountEdit_OnClick");
        Check(form.Visibility == Visibility.Collapsed && email.Text == original.AccountEmail && password.Password.Length == 0,
            "canceling an account edit restores the bound email and clears the password");
        ClickSetting(dialog, "EditAccount_OnClick");
        email.Text = "new@example.invalid";
        password.Password = "synthetic";
        ClickSetting(dialog, "BindAccountButton_OnClick");
        Check(bindingCalls == 1 && dialog.AccountBindingChanged && password.Password.Length == 0 && form.Visibility == Visibility.Collapsed,
            "binding updates the visible identity and hides the cleared password form");

        var combo = Find<ComboBox>(dialog, "SiteCombo");
        var custom = Find<TextBox>(dialog, "CustomSiteBox");
        var count = combo.Items.Count;
        custom.Text = "https://user@invalid.example.invalid:8080/watch?v=9";
        ClickSetting(dialog, "AddCustomSite_OnClick");
        Check(combo.Items.Count == count, "invalid custom sites show feedback without changing the list");
        custom.Text = "https://NEW.example.invalid/watch?v=9";
        ClickSetting(dialog, "ConfirmButton_OnClick");
        Check(ReferenceEquals(pages.SelectedItem, sitePage) && Find<TextBlock>(dialog, "SettingsFeedbackText").Text.Contains("尚未添加"),
            "saving cannot silently discard an unsubmitted custom-site draft");
        ClickSetting(dialog, "AddCustomSite_OnClick");
        Check(combo.Items.Count == count + 1 && (string?)combo.SelectedValue == "new.example.invalid" &&
              Find<StackPanel>(dialog, "AccountSiteMismatchPanel").Visibility == Visibility.Visible && form.Visibility == Visibility.Collapsed,
            "pasted links add only the domain and expose the site-switch account guidance");
        custom.Text = "new.example.invalid";
        ClickSetting(dialog, "AddCustomSite_OnClick");
        Check(combo.Items.Count == count + 1, "adding an existing domain selects it without creating a duplicate");
        ClickSetting(dialog, "RemoveCustomSite_OnClick");
        Check(combo.Items.Count == count && (string?)combo.SelectedValue == original.SiteHost && !Find<Button>(dialog, "RemoveCustomSiteButton").IsEnabled,
            "removing a custom site falls back to the active site and preset removal stays unavailable");
        custom.Text = "new.example.invalid";
        ClickSetting(dialog, "AddCustomSite_OnClick");
        ClickSetting(dialog, "ConfirmButton_OnClick");
        Check(ReferenceEquals(pages.SelectedItem, sitePage) && Find<TextBlock>(dialog, "SettingsFeedbackText").Text.Contains("本地收藏"),
            "incompatible site/account edits point to the relevant page without a message box");
        ClickSetting(dialog, "UseLocalFavorites_OnClick");
        Check(Find<RadioButton>(dialog, "FavoritesLocalRadio").IsChecked == true &&
              Find<GroupBox>(dialog, "AccountOptionsPanel").Visibility == Visibility.Collapsed,
            "the site-switch guidance can switch the draft to local favorites directly");
        pages.SelectedItem = appearancePage;
        var large = Find<CheckBox>(dialog, "LargeSearchResultCoversCheckBox");
        Check(!large.IsEnabled && large.IsChecked == true, "disabling covers preserves the large-cover preference without exposing an inactive control");

        var editor = Find<NamingRuleEditor>(dialog, "NamingEditor");
        editor.RuleText = "{unknown}";
        ClickSetting(dialog, "ConfirmButton_OnClick");
        Check(ReferenceEquals(pages.SelectedItem, namingPage), "invalid naming rule returns the user to its own category");
        ClickSetting(dialog, "ResetNaming_OnClick");
        dialog.Loaded += (_, _) => ClickSetting(dialog, "ConfirmButton_OnClick");
        Check(dialog.ShowDialog() == true && dialog.Settings.SiteHost == "new.example.invalid" &&
              dialog.Settings.AccountUserId.Length == 0 && dialog.Settings.FavoritesMode == AppSettings.LocalFavoritesMode &&
              original.CustomSiteHosts.SequenceEqual(new[] { "saved.example.invalid" }) && original.AccountUserId == "17",
            "saving applies the chosen site and clears old-site binding without mutating the original settings");
    }
}
