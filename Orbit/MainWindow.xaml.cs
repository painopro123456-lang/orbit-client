using System.Windows;
using System.Windows.Input;
namespace Orbit;
public partial class MainWindow : Window
{
 public MainWindow() { InitializeComponent(); }
 private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize_Click(sender,e); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
 private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
 private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
 private void Close_Click(object sender, RoutedEventArgs e) => Close();
 private void SetPage(string eyebrow, string title, string subtitle) { PageEyebrow.Text = eyebrow; PageTitle.Text = title; PageSubtitle.Text = subtitle; }
 private void Home_Click(object sender, RoutedEventArgs e) => SetPage("YOUR SPACE","Welcome back.","Everything you need for your next world, in one place.");
 private void Instances_Click(object sender, RoutedEventArgs e) => SetPage("YOUR LIBRARY","Instances","Keep vanilla, modded and modpack installations separate.");
 private void Mods_Click(object sender, RoutedEventArgs e) => SetPage("CUSTOMIZE YOUR GAME","Mods & Modpacks","Discover and organize content for your Minecraft instances.");
 private void Browse_Click(object sender, RoutedEventArgs e) => SetPage("DISCOVER","Browse","Explore modpacks, mods and supported game content.");
 private void Settings_Click(object sender, RoutedEventArgs e) => SetPage("PREFERENCES","Settings","Control Java, memory, resolution, updates and launcher behavior.");
 private void SignIn_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Microsoft sign-in is the next integration step. No account credentials are requested by this preview.", "ORBIT", MessageBoxButton.OK, MessageBoxImage.Information);
 private void Play_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Create an instance and connect your Microsoft account before launching Minecraft. Game launching is not wired up in this initial preview.", "ORBIT", MessageBoxButton.OK, MessageBoxImage.Information);
}