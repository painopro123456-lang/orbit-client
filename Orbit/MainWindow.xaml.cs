using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.ProcessBuilder;
using Microsoft.Identity.Client;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.Msal.OAuth;

namespace Orbit;

public partial class MainWindow : Window
{
    private const string MicrosoftClientId = "ff7ba7c9-32f2-4d76-84a0-47ca577f2097";
    private const string MicrosoftRedirectUri = "http://localhost:53682/callback";
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT");

    private JELoginHandler? _loginHandler;
    private MSession? _session;
    private MinecraftLauncher? _minecraftLauncher;
    private Process? _gameProcess;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DataDirectory);
        await RestoreAccountAsync();
        await LoadVersionsAsync();
    }

    private async Task<JELoginHandler> GetLoginHandlerAsync()
    {
        if (_loginHandler is not null)
            return _loginHandler;

        var authDirectory = Path.Combine(DataDirectory, "auth");
        Directory.CreateDirectory(authDirectory);

        var msal = PublicClientApplicationBuilder
            .Create(MicrosoftClientId)
            .WithTenantId("consumers")
            .WithRedirectUri(MicrosoftRedirectUri)
            .Build();

        await MsalClientHelper.RegisterCache(msal, new MsalCacheSettings
        {
            CacheFileName = "orbit-msal-cache.bin",
            CacheDir = authDirectory
        });

        _loginHandler = new JELoginHandlerBuilder()
            .WithOAuthProvider(new MsalCodeFlowProvider(msal))
            .WithAccountManager(Path.Combine(authDirectory, "minecraft-accounts.json"))
            .Build();

        return _loginHandler;
    }

    private async Task RestoreAccountAsync()
    {
        try
        {
            SetActivity("Checking for a saved Microsoft account…");
            var handler = await GetLoginHandlerAsync();
            _session = await handler.AuthenticateSilently();
            UpdateAccountUi();
            SetActivity($"Signed in as {_session.Username}. Ready to play.");
        }
        catch
        {
            _session = null;
            UpdateAccountUi();
            SetActivity("Connect your Microsoft account to play Minecraft Java.");
        }
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            SetActivity("Loading Minecraft versions…");
            _minecraftLauncher = new MinecraftLauncher(
                new MinecraftPath(Path.Combine(DataDirectory, "game")));

            var versions = await _minecraftLauncher.GetAllVersionsAsync();
            var available = versions.Select(v => v.Name).Distinct().ToList();

            VersionSelector.ItemsSource = available;
            if (available.Contains("26.2"))
                VersionSelector.SelectedItem = "26.2";
            else if (available.Count > 0)
                VersionSelector.SelectedIndex = 0;

            SelectedVersionText.Text = VersionSelector.SelectedItem?.ToString() ?? "No versions found";
            SetActivity(available.Contains("26.2")
                ? "Minecraft 26.2 is available. Connect your account and press Play."
                : "Version list loaded. The 26.2 target is not currently present in Mojang's version manifest.");
        }
        catch (Exception ex)
        {
            SetActivity("Could not load Minecraft versions: " + ex.Message);
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_session is not null)
        {
            var choice = MessageBox.Show(
                "Disconnect the current Minecraft account from ORBIT?",
                "ORBIT account",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (choice != MessageBoxResult.Yes)
                return;

            try
            {
                SetBusy(true, "Disconnecting Microsoft account…");
                var handler = await GetLoginHandlerAsync();
                await handler.Signout();
                _session = null;
                UpdateAccountUi();
                SetActivity("Account disconnected.");
            }
            catch (Exception ex)
            {
                ShowError("Could not disconnect the account", ex);
            }
            finally
            {
                SetBusy(false);
            }
            return;
        }

        try
        {
            SetBusy(true, "Opening secure Microsoft sign-in…");
            var handler = await GetLoginHandlerAsync();
            _session = await handler.AuthenticateInteractively();
            UpdateAccountUi();
            SetActivity($"Minecraft Java account verified: {_session.Username}");
            MessageBox.Show(
                $"Signed in successfully as {_session.Username}.\nMinecraft Java access was verified by Microsoft/Minecraft services.",
                "ORBIT",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _session = null;
            UpdateAccountUi();
            ShowError("Microsoft sign-in failed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_session is null)
        {
            MessageBox.Show(
                "Connect a Microsoft account that owns Minecraft: Java Edition before launching.",
                "ORBIT",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var version = VersionSelector.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(version))
        {
            MessageBox.Show("Wait for the Minecraft version list to load, then select a version.", "ORBIT");
            return;
        }

        if (_gameProcess is { HasExited: false })
        {
            MessageBox.Show("Minecraft is already running.", "ORBIT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true, $"Preparing Minecraft {version}…");
            _minecraftLauncher ??= new MinecraftLauncher(
                new MinecraftPath(Path.Combine(DataDirectory, "game")));

            _minecraftLauncher.FileProgressChanged += (_, args) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InstallProgress.Maximum = Math.Max(1, args.TotalTasks);
                    InstallProgress.Value = Math.Min(args.ProgressedTasks, InstallProgress.Maximum);
                    ActivityText.Text = $"Downloading {args.Name}  •  {args.ProgressedTasks}/{args.TotalTasks}";
                }));
            };

            await _minecraftLauncher.InstallAsync(version);

            SetActivity($"Starting Minecraft {version}…");
            InstallProgress.Value = InstallProgress.Maximum;

            var options = new MLaunchOption
            {
                Session = _session,
                MaximumRamMb = 4096,
                MinimumRamMb = 1024,
                GameLauncherName = "ORBIT",
                GameLauncherVersion = "0.2.0"
            };

            var process = await _minecraftLauncher.BuildProcessAsync(version, options);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                SetActivity($"Minecraft closed (exit code {SafeExitCode(process)}).");
                SetBusy(false);
                InstallProgress.Value = 0;
            }));

            if (!process.Start())
                throw new InvalidOperationException("Windows could not start the Minecraft Java process.");

            _gameProcess = process;
            SetActivity($"Minecraft {version} is running. You can return to ORBIT.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not launch Minecraft {version}", ex);
            SetActivity("Launch failed. Check the error details and try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    private void UpdateAccountUi()
    {
        var connected = _session is not null;
        AccountName.Text = connected ? _session!.Username : "Guest player";
        AccountStatus.Text = connected ? "Minecraft Java verified" : "Microsoft account";
        AccountButton.Content = connected ? "Disconnect account" : "Connect account  ↗";
        ConnectionBadgeText.Text = connected ? "MINECRAFT ACCOUNT CONNECTED" : "ACCOUNT NOT CONNECTED";
        ConnectionBadgeDot.Fill = connected
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Goldenrod;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        PlayButton.IsEnabled = !busy;
        AccountButton.IsEnabled = !busy;
        if (message is not null)
            SetActivity(message);
    }

    private void SetActivity(string message)
    {
        ActivityText.Text = message;
    }

    private static void ShowError(string title, Exception exception)
    {
        var message = exception.Message;
        if (exception.InnerException is not null)
            message += "\n\n" + exception.InnerException.Message;

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            Maximize_Click(sender, e);
        else if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetPage(string eyebrow, string title, string subtitle)
    {
        PageEyebrow.Text = eyebrow;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private void Home_Click(object sender, RoutedEventArgs e) =>
        SetPage("YOUR SPACE", "Welcome back.", "Everything you need for your next world, in one place.");

    private void Instances_Click(object sender, RoutedEventArgs e) =>
        SetPage("YOUR LIBRARY", "Instances", "Instance management is the next ORBIT milestone.");

    private async void Mods_Click(object sender, RoutedEventArgs e)
    {
        SetPage("CUSTOMIZE YOUR GAME", "Mod Manager", "Browse Modrinth mods and manage instance files.");
        try
        {
            using var results = await ModrinthService.SearchAsync("", "mod");
            var titles = results.RootElement.GetProperty("hits").EnumerateArray()
                .Select(hit => hit.GetProperty("title").GetString()).Take(12);
            MessageBox.Show(string.Join(Environment.NewLine, titles), "ORBIT - Modrinth mods");
        }
        catch (Exception ex) { ShowError("Modrinth search failed", ex); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        SetPage("DISCOVER", "Modpacks", "Browse Modrinth modpacks.");
        try
        {
            using var results = await ModrinthService.SearchAsync("", "modpack");
            var titles = results.RootElement.GetProperty("hits").EnumerateArray()
                .Select(hit => hit.GetProperty("title").GetString()).Take(12);
            MessageBox.Show(string.Join(Environment.NewLine, titles), "ORBIT - Modrinth modpacks");
        }
        catch (Exception ex) { ShowError("Modrinth search failed", ex); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SetPage("PREFERENCES", "Settings", "Java and memory settings are being connected to the game runtime.");

    private void VersionSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedVersionText is not null && VersionSelector.SelectedItem is not null)
            SelectedVersionText.Text = VersionSelector.SelectedItem.ToString()!;
    }
}
