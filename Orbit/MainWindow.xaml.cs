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
        SetPage("CUSTOMIZE YOUR GAME", "Mod Manager", "Browse and install mods from Modrinth or CurseForge.");
        var provider = MessageBox.Show("Choose your mod source.\n\nYes = Modrinth\nNo = CurseForge", "ORBIT Mod Manager", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (provider == MessageBoxResult.Yes) await SearchAndInstallAsync("mod");
        else if (provider == MessageBoxResult.No) await SearchAndInstallCurseForgeAsync(false);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        SetPage("DISCOVER", "Modpacks", "Browse and install modpacks from Modrinth or CurseForge.");
        var provider = MessageBox.Show("Choose your modpack source.\n\nYes = Modrinth\nNo = CurseForge", "ORBIT Modpacks", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (provider == MessageBoxResult.Yes) await SearchAndInstallAsync("modpack");
        else if (provider == MessageBoxResult.No) await SearchAndInstallCurseForgeAsync(true);
    }

    private async Task SearchAndInstallAsync(string projectType)
    {
        try
        {
            var query = Microsoft.VisualBasic.Interaction.InputBox("Search Modrinth:", "ORBIT - Modrinth", "");
            if (string.IsNullOrWhiteSpace(query)) return;
            using var search = await ModrinthService.SearchAsync(query, projectType);
            var hits = search.RootElement.GetProperty("hits").EnumerateArray().Take(10).ToList();
            if (hits.Count == 0) { MessageBox.Show("No results found."); return; }
            var labels = hits.Select((h, i) => (i + 1) + ". " + h.GetProperty("title").GetString());
            var pick = Microsoft.VisualBasic.Interaction.InputBox(string.Join("\n", labels) + "\n\nEnter a number to install:", "Choose content", "1");
            if (!int.TryParse(pick, out var n) || n < 1 || n > hits.Count) return;
            var project = hits[n - 1];
            var id = project.GetProperty("project_id").GetString()!;
            var name = project.GetProperty("title").GetString() ?? id;
            using var versions = await ModrinthService.VersionsAsync(id);
            var gameVersion = VersionSelector.SelectedItem?.ToString() ?? "26.2";
            var v = versions.RootElement.EnumerateArray().FirstOrDefault(x =>
                x.GetProperty("game_versions").EnumerateArray().Any(g => g.GetString() == gameVersion));
            if (v.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                v = versions.RootElement.EnumerateArray().FirstOrDefault();
            if (v.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                throw new InvalidDataException("No downloadable version is available for this project.");
            var file = v.GetProperty("files").EnumerateArray().First();
            var url = file.GetProperty("url").GetString()!;
            var filename = file.GetProperty("filename").GetString()!;
            var root = Path.Combine(DataDirectory, "instances");
            Directory.CreateDirectory(root);
            if (projectType == "modpack")
            {
                var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mrpack");
                await ModrinthService.DownloadAsync(url, temp);
                var folder = Path.Combine(root, MakeSafeInstanceName(name));
                await ModrinthService.InstallPackAsync(temp, folder);
                File.Delete(temp);
                MessageBox.Show("Modpack files installed to:\n" + folder + "\n\nLoader installation and launching modpacks are not connected yet.", "ORBIT");
            }
            else
            {
                var folder = Path.Combine(root, "Default-" + gameVersion, "mods");
                await ModrinthService.DownloadAsync(url, Path.Combine(folder, filename));
                MessageBox.Show("Installed " + filename + ". A compatible mod loader is required to run it.", "ORBIT");
            }
        }
        catch (Exception ex) { ShowError("Modrinth install failed", ex); }
    }

    private async Task SearchAndInstallCurseForgeAsync(bool modpack)
    {
        try
        {
            if (!CurseForgeService.HasApiKey)
            {
                var setup = MessageBox.Show("CurseForge requires your own approved API key. Add it in ORBIT Settings first?\n\nOpen Settings now?", "CurseForge setup", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (setup == MessageBoxResult.Yes) Settings_Click(this, new RoutedEventArgs());
                if (!CurseForgeService.HasApiKey) return;
            }

            var query = Microsoft.VisualBasic.Interaction.InputBox(
                modpack ? "Search CurseForge modpacks:" : "Search CurseForge mods:",
                modpack ? "ORBIT - CurseForge Modpacks" : "ORBIT - CurseForge Mods", "");
            if (string.IsNullOrWhiteSpace(query)) return;

            SetActivity("Searching CurseForge…");
            var projects = await CurseForgeService.SearchAsync(query, modpack, null);
            if (projects.Count == 0) { MessageBox.Show("No CurseForge results were found.", "ORBIT"); return; }
            var labels = projects.Take(12).Select((p, i) => (i + 1) + ". " + p.Name + (string.IsNullOrWhiteSpace(p.Summary) ? "" : " — " + p.Summary));
            var selection = Microsoft.VisualBasic.Interaction.InputBox(
                string.Join("\n\n", labels) + "\n\nEnter a number to install:",
                "Choose CurseForge content", "1");
            if (!int.TryParse(selection, out var selected) || selected < 1 || selected > Math.Min(12, projects.Count)) return;

            var project = projects[selected - 1];
            var gameVersion = VersionSelector.SelectedItem?.ToString() ?? "26.2";
            var instanceRoot = Path.Combine(DataDirectory, "instances");
            Directory.CreateDirectory(instanceRoot);
            var instanceName = MakeSafeInstanceName(project.Name);
            var folder = Path.Combine(instanceRoot, instanceName);

            SetActivity("Installing " + project.Name + " from CurseForge…");
            if (modpack)
            {
                await CurseForgeService.InstallModpackAsync(project, gameVersion, folder);
                SetActivity("CurseForge modpack files installed. Loader setup and launch support are the next step.");
                MessageBox.Show($"Installed {project.Name} to:\n{folder}\n\nORBIT still needs to install the pack's Forge/Fabric/NeoForge loader and connect this instance to Play.", "ORBIT");
            }
            else
            {
                var modsFolder = Path.Combine(instanceRoot, "Default-" + gameVersion, "mods");
                await CurseForgeService.InstallModAsync(project, gameVersion, modsFolder);
                SetActivity("Installed " + project.Name + " from CurseForge.");
                MessageBox.Show($"Installed {project.Name} into Default-{gameVersion}.\n\nA compatible mod loader is required to load this mod.", "ORBIT");
            }
        }
        catch (Exception ex)
        {
            SetActivity("CurseForge operation failed.");
            ShowError("CurseForge install failed", ex);
        }
    }

    private static string MakeSafeInstanceName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '-');
        return string.IsNullOrWhiteSpace(value) ? "New-Modpack" : value.Trim();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SetPage("PREFERENCES", "Settings", "Configure your game and content providers.");
        var action = MessageBox.Show(
            "CurseForge API key settings\n\nYes = Add or replace API key\nNo = Remove saved key\nCancel = Keep current setting",
            "ORBIT Settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (action == MessageBoxResult.Yes)
        {
            var key = Microsoft.VisualBasic.Interaction.InputBox(
                "Paste your approved CurseForge API key. It will be encrypted for this Windows account and stored locally, not committed to GitHub.",
                "CurseForge API key", "");
            if (!string.IsNullOrWhiteSpace(key))
            {
                CurseForgeService.SaveApiKey(key);
                MessageBox.Show("CurseForge API key saved securely for this Windows account.", "ORBIT");
            }
        }
        else if (action == MessageBoxResult.No)
        {
            CurseForgeService.SaveApiKey("");
            MessageBox.Show("Saved CurseForge API key removed.", "ORBIT");
        }
    }

    private void VersionSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedVersionText is not null && VersionSelector.SelectedItem is not null)
            SelectedVersionText.Text = VersionSelector.SelectedItem.ToString()!;
    }
}
