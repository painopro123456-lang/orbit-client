using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.Json;
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
    private string _modpackSource = "Modrinth";
    private readonly ObservableCollection<ModpackSearchResult> _searchResults = new();
    private readonly ObservableCollection<InstalledModItem> _installedMods = new();

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

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        HomePanel.Visibility = Visibility.Visible;
        LibraryPanel.Visibility = Visibility.Collapsed;
        SetPage("YOUR SPACE", "Welcome back.", "Everything you need for your next world, in one place.");
    }

    private void Instances_Click(object sender, RoutedEventArgs e) => ShowLibraryPage(false);

    private void Mods_Click(object sender, RoutedEventArgs e) => ShowLibraryPage(true);

    private void Browse_Click(object sender, RoutedEventArgs e) => ShowLibraryPage(true);

    private void ShowLibraryPage(bool showModpacks)
    {
        HomePanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        InstalledModsPanel.Visibility = Visibility.Collapsed;
        SetPage("YOUR LIBRARY", "Instances", "Your game profiles and installed modpacks, together.");
        if (showModpacks) ShowModpacksTab();
        else ShowInstancesTab();
    }

    private void InstancesTab_Click(object sender, RoutedEventArgs e) => ShowInstancesTab();

    private void ModpacksTab_Click(object sender, RoutedEventArgs e) => ShowModpacksTab();

    private void ShowInstancesTab()
    {
        InstancesTabPanel.Visibility = Visibility.Visible;
        ModpacksTabPanel.Visibility = Visibility.Collapsed;
        InstalledModsPanel.Visibility = Visibility.Collapsed;
        InstancesTabButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 36, 64));
        InstancesTabButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 90, 150));
        ModpacksTabButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        RefreshInstances();
    }

    private void ShowModpacksTab()
    {
        InstancesTabPanel.Visibility = Visibility.Collapsed;
        ModpacksTabPanel.Visibility = Visibility.Visible;
        InstalledModsPanel.Visibility = Visibility.Collapsed;
        ModpacksTabButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 36, 64));
        ModpacksTabButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 90, 150));
        InstancesTabButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        SetModpackSource(_modpackSource);
    }

    private void ModrinthSource_Click(object sender, RoutedEventArgs e) => SetModpackSource("Modrinth");

    private void CurseForgeSource_Click(object sender, RoutedEventArgs e) => SetModpackSource("CurseForge");

    private void SetModpackSource(string source)
    {
        _modpackSource = source;
        var selected = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 36, 64));
        var normal = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 34, 48));
        ModrinthSourceButton.Background = source == "Modrinth" ? selected : normal;
        CurseForgeSourceButton.Background = source == "CurseForge" ? selected : normal;
        ModrinthSourceButton.BorderBrush = source == "Modrinth" ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 90, 150)) : normal;
        CurseForgeSourceButton.BorderBrush = source == "CurseForge" ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 90, 150)) : normal;
        ModpackSourceTitle.Text = source + " modpacks";
        ModpackSourceDescription.Text = source == "Modrinth"
            ? "Search Modrinth and install a modpack into its own instance."
            : "Search CurseForge and install a modpack into its own instance.";
    }

    private async void BrowseModpacks_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryPage(true);
        await SearchContentAsync();
    }

    private async void SearchContent_Click(object sender, RoutedEventArgs e) => await SearchContentAsync();

    private async Task SearchContentAsync()
    {
        if (_busy) return;
        var query = ContentSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchStatusText.Text = "Enter a name or keyword to search modpacks.";
            return;
        }

        try
        {
            SetBusy(true, "Searching " + _modpackSource + "…");
            SearchResultsList.ItemsSource = null;
            _searchResults.Clear();
            if (_modpackSource == "Modrinth")
            {
                using var document = await ModrinthService.SearchAsync(query, "modpack");
                foreach (var hit in document.RootElement.GetProperty("hits").EnumerateArray())
                {
                    _searchResults.Add(new ModpackSearchResult
                    {
                        Id = hit.GetProperty("project_id").GetString() ?? "",
                        Name = hit.GetProperty("title").GetString() ?? "Unnamed modpack",
                        Summary = hit.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                        Meta = $"Modrinth  •  {ReadInt(hit, "downloads"):N0} downloads",
                        Source = "Modrinth"
                    });
                }
            }
            else
            {
                if (!CurseForgeService.HasApiKey)
                    throw new InvalidOperationException("CurseForge requires an approved API key. Add it in Settings first.");
                var projects = await CurseForgeService.SearchAsync(query, true, VersionSelector.SelectedItem?.ToString());
                foreach (var project in projects)
                    _searchResults.Add(new ModpackSearchResult
                    {
                        Id = project.Id.ToString(),
                        Name = project.Name,
                        Summary = project.Summary,
                        Meta = "CurseForge  •  Modpack",
                        Source = "CurseForge",
                        CurseForgeProject = project
                    });
            }

            SearchResultsList.ItemsSource = _searchResults;
            SearchStatusText.Text = _searchResults.Count == 0
                ? "No modpacks found. Try another search."
                : $"{_searchResults.Count} modpacks found. Select one, then install it.";
            SetActivity(_searchResults.Count == 0 ? "No matching modpacks." : "Modpack search complete.");
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = "Search failed: " + ex.Message;
            SetActivity("Modpack search failed.");
            ShowError("ORBIT search failed", ex);
        }
        finally { SetBusy(false); }
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private void SearchResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        InstallSelectedButton.IsEnabled = SearchResultsList.SelectedItem is not null && !_busy;
    }

    private async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (SearchResultsList.SelectedItem is not ModpackSearchResult selected)
        {
            SearchStatusText.Text = "Select a modpack first.";
            return;
        }

        var root = Path.Combine(DataDirectory, "instances");
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, MakeSafeInstanceName(selected.Name));
        if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length > 0)
        {
            MessageBox.Show("An instance with this name already exists. Rename or remove it before installing this pack again.", "ORBIT");
            return;
        }

        try
        {
            SetBusy(true, "Installing " + selected.Name + "…");
            InstallSelectedButton.IsEnabled = false;
            if (selected.Source == "Modrinth")
            {
                using var versions = await ModrinthService.VersionsAsync(selected.Id);
                var gameVersion = VersionSelector.SelectedItem?.ToString() ?? "26.2";
                var compatible = versions.RootElement.EnumerateArray().FirstOrDefault(v =>
                    v.TryGetProperty("game_versions", out var gameVersions) &&
                    gameVersions.EnumerateArray().Any(g => g.GetString() == gameVersion));
                if (compatible.ValueKind == JsonValueKind.Undefined)
                    compatible = versions.RootElement.EnumerateArray().FirstOrDefault();
                if (compatible.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidDataException("No downloadable version is available for this modpack.");
                var file = compatible.GetProperty("files").EnumerateArray()
                    .FirstOrDefault(f => f.TryGetProperty("primary", out var primary) && primary.GetBoolean());
                if (file.ValueKind == JsonValueKind.Undefined)
                    file = compatible.GetProperty("files").EnumerateArray().First();
                var url = file.GetProperty("url").GetString()!;
                var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mrpack");
                try
                {
                    await ModrinthService.DownloadAsync(url, temp);
                    await ModrinthService.InstallPackAsync(temp, folder);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            else
            {
                if (selected.CurseForgeProject is null)
                    throw new InvalidDataException("The selected CurseForge project metadata is missing.");
                await CurseForgeService.InstallModpackAsync(selected.CurseForgeProject,
                    VersionSelector.SelectedItem?.ToString() ?? "26.2", folder);
            }

            SearchStatusText.Text = $"Installed {selected.Name}. Instance created in your library.";
            SetActivity($"Installed {selected.Name}. Loader setup is still required before this pack can launch.");
            MessageBox.Show($"Installed {selected.Name}.\n\nThe files are in your ORBIT instances library. This release does not yet install the pack's Fabric/Forge/NeoForge runtime automatically, so the pack may not launch until that loader integration is added.", "ORBIT");
            RefreshInstances();
        }
        catch (Exception ex)
        {
            SetActivity("Modpack installation failed.");
            SearchStatusText.Text = "Install failed: " + ex.Message;
            ShowError("Modpack installation failed", ex);
        }
        finally
        {
            SetBusy(false);
            InstallSelectedButton.IsEnabled = SearchResultsList.SelectedItem is not null;
        }
    }

    private void OpenInstalledMods_Click(object sender, RoutedEventArgs e)
    {
        InstancesTabPanel.Visibility = Visibility.Collapsed;
        ModpacksTabPanel.Visibility = Visibility.Collapsed;
        InstalledModsPanel.Visibility = Visibility.Visible;
        SetPage("YOUR LIBRARY", "Installed mods", "Enable, disable and remove JAR files in each instance.");
        RefreshModInstanceChoices();
    }

    private void BackToModpacks_Click(object sender, RoutedEventArgs e) => ShowModpacksTab();

    private void RefreshModInstanceChoices()
    {
        var root = Path.Combine(DataDirectory, "instances");
        Directory.CreateDirectory(root);
        var folders = Directory.GetDirectories(root).Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n)).OrderBy(n => n).ToList();
        ModInstanceSelector.ItemsSource = folders;
        if (folders.Count > 0 && ModInstanceSelector.SelectedIndex < 0) ModInstanceSelector.SelectedIndex = 0;
        RefreshInstalledMods();
    }

    private void ModInstanceSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshInstalledMods();

    private string? SelectedModsDirectory()
    {
        var instance = ModInstanceSelector.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(instance)) return null;
        return Path.Combine(DataDirectory, "instances", instance, "mods");
    }

    private void RefreshInstalledMods()
    {
        if (InstalledModsList is null) return;
        _installedMods.Clear();
        var folder = SelectedModsDirectory();
        if (folder is not null && Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder).Where(f =>
                f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
            {
                var disabled = file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
                var info = new FileInfo(file);
                _installedMods.Add(new InstalledModItem
                {
                    Path = file,
                    Name = Path.GetFileName(file).Replace(".jar.disabled", "", StringComparison.OrdinalIgnoreCase),
                    State = disabled ? "Disabled" : "Enabled",
                    FileSize = $"{Math.Max(1, info.Length / 1024):N0} KB",
                    Disabled = disabled
                });
            }
        }
        InstalledModsList.ItemsSource = _installedMods;
    }

    private void ToggleMod_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledModsList.SelectedItem is not InstalledModItem mod) return;
        var target = mod.Disabled ? mod.Path[..^".disabled".Length] : mod.Path + ".disabled";
        try { File.Move(mod.Path, target); RefreshInstalledMods(); }
        catch (Exception ex) { ShowError("Could not change mod state", ex); }
    }

    private void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledModsList.SelectedItem is not InstalledModItem mod) return;
        var confirm = MessageBox.Show($"Remove {mod.Name} from this instance?", "Remove mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try { File.Delete(mod.Path); RefreshInstalledMods(); }
        catch (Exception ex) { ShowError("Could not remove mod", ex); }
    }

    private void RefreshInstances_Click(object sender, RoutedEventArgs e) => RefreshInstances();

    private void RefreshInstances()
    {
        var root = Path.Combine(DataDirectory, "instances");
        Directory.CreateDirectory(root);
        var folders = Directory.GetDirectories(root)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.GetFileName(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        InstanceList.ItemsSource = folders;
        InstancesEmptyText.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

internal sealed class ModpackSearchResult
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Source { get; init; } = "";
    public CurseForgeService.Project? CurseForgeProject { get; init; }
}

internal sealed class InstalledModItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string FileSize { get; init; } = "";
    public bool Disabled { get; init; }
}
