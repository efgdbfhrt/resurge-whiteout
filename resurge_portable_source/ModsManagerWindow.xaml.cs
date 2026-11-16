using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

internal sealed record ModItem(string Title, string Type, ImageSource? IconImage, string Status, Brush StatusColor,
    string WorkshopJsonPath, string DirectoryPath, bool NeedsFix);

public partial class ModsManagerWindow : Window
{
    private readonly string _gamePath;

    public ModsManagerWindow(string gamePath)
    {
        InitializeComponent();
        _gamePath = gamePath;
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void RefreshList()
    {
        var items = new List<ModItem>();
        try
        {
            foreach (var (folder, type) in new[] { ("mods", "Mod"), ("usermaps", "Map") })
            {
                string root = Path.Combine(_gamePath, folder);
                if (!Directory.Exists(root)) continue;
                WorkshopService.RejectLink(root);

                foreach (string item in Directory.EnumerateDirectories(root))
                {
                    if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) continue;
                    var metadata = ReadMetadata(item);
                    string name = Path.GetFileName(item);
                    bool needsFix = (metadata is not null && !name.Equals(metadata.Folder, StringComparison.OrdinalIgnoreCase)) ||
                        Directory.EnumerateFiles(item).Any(IsLooseFastFile);
                    string json = WorkshopService.MetadataPath(item);
                    items.Add(new ModItem(metadata?.Title ?? name, type, ReadThumbnail(item), needsFix ? "Needs repair" : "Ready",
                        needsFix ? Brushes.Salmon : Brushes.LightGreen, File.Exists(json) ? json : "No workshop.json", item, needsFix));
                }
            }
            ModsListBox.ItemsSource = items;
        }
        catch (Exception ex) { ShowResult(ex.Message, true); }
    }

    private static WorkshopItem? ReadMetadata(string item)
    {
        try { return WorkshopService.ReadItem(item); }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static bool IsLooseFastFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ff" or ".fd";

    private static ImageSource? ReadThumbnail(string item)
    {
        try
        {
            string? path = Directory.EnumerateFiles(item).FirstOrDefault(file =>
                Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg");
            if (path is null) return null;
            WorkshopService.RejectLink(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 160;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception) { return null; }
    }

    private void FixAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModsListBox.ItemsSource is not List<ModItem> items) return;
        try
        {
            using var folderLock = GameLauncher.LockFolder(_gamePath);
            int fixedCount = 0;
            var errors = new List<string>();
            foreach (var item in items.Where(item => item.NeedsFix))
            {
                try
                {
                    FixDirectory(item.DirectoryPath);
                    fixedCount++;
                }
                catch (IOException ex) { errors.Add($"{item.Title}: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { errors.Add($"{item.Title}: {ex.Message}"); }
            }
            ShowResult(errors.Count == 0 ? $"Repaired {fixedCount} item(s)." :
                $"Repaired {fixedCount}; skipped {errors.Count}.\n{errors[0]}", errors.Count > 0);
            RefreshList();
        }
        catch (Exception ex) { ShowResult(ex.Message, true); }
    }

    private void ShowResult(string message, bool warning) => MessageBox.Show(this, message, "Workshop repair",
        MessageBoxButton.OK, warning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    private void FixDirectory(string item)
    {
        string parent = Path.GetDirectoryName(item)!;
        if (parent != Path.Combine(_gamePath, "mods") && parent != Path.Combine(_gamePath, "usermaps"))
            throw new IOException("Item is outside the selected Workshop library.");
        WorkshopService.RejectLink(parent);
        WorkshopService.RejectLink(item);

        var metadata = ReadMetadata(item);
        string renamed = metadata is null ? item : Path.Combine(parent, metadata.Folder);
        bool rename = !renamed.Equals(item, StringComparison.OrdinalIgnoreCase);
        if (rename && (Directory.Exists(renamed) || File.Exists(renamed)))
            throw new IOException("The expected folder already exists.");

        string zone = Path.Combine(item, "zone");
        if (Directory.Exists(zone)) WorkshopService.RejectLink(zone);
        var files = Directory.EnumerateFiles(item).Where(file => IsLooseFastFile(file) ||
            Path.GetExtension(file).Equals(".xpak", StringComparison.OrdinalIgnoreCase)).ToArray();
        var moves = files.Select(file => (Source: file, Target: Path.Combine(zone, Path.GetFileName(file)))).ToArray();
        foreach (var move in moves)
        {
            WorkshopService.RejectLink(move.Source);
            if (File.Exists(move.Target) || Directory.Exists(move.Target))
                throw new IOException($"Existing zone file would be overwritten: {Path.GetFileName(move.Target)}");
        }

        int moved = 0;
        try
        {
            if (moves.Length > 0) Directory.CreateDirectory(zone);
            foreach (var move in moves)
            {
                File.Move(move.Source, move.Target);
                moved++;
            }
            if (rename) Directory.Move(item, renamed);
        }
        catch
        {
            foreach (var move in moves.Take(moved).Reverse()) File.Move(move.Target, move.Source);
            throw;
        }
    }
}
