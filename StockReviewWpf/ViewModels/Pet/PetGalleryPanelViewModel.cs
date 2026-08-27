using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using StockReviewWpf.Models;
using StockReviewWpf.Services;

namespace StockReviewWpf.ViewModels.Pet;

/// <summary>
/// 宠物画廊面板 ViewModel - 对应 PetGalleryPanel.vue + petAppearanceStore.js
/// 已安装列表：扫描 DataDir\pets；在线目录：awesome-codex-pet pets.json；
/// 激活状态持久化：appConfig.activePetId；未激活时兜底默认流萤。
/// </summary>
public partial class PetGalleryPanelViewModel : ObservableObject
{
    private const string DefaultPetId = "firefly--lingxiaotian";

    private readonly PetManagementService? _petService;
    private readonly List<PetCatalogItem> _allItems = new();
    private string? _activePetId;

    // ============ 缩略图懒加载（对齐原版：打开只拉前 30 个，滚动到底部再取下一批 30 个） ============
    private const int ThumbBatchSize = 30;
    /// <summary>已授权下载缩略图的条数上限（当前视图顺序），滚动到底部附近时 +30。</summary>
    private int _thumbBudget = ThumbBatchSize;
    /// <summary>已发起过下载的 slug（含失败，避免重复下载；刷新目录时清空重试）。</summary>
    private readonly HashSet<string> _thumbQueued = new();

    [ObservableProperty]
    private ObservableCollection<PetCatalogItem> _catalogItems = new();

    [ObservableProperty]
    private PetCatalogItem? _selectedItem;

    [ObservableProperty]
    private string _searchKeyword = "";

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private int _installedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string? _activePetName;

    [ObservableProperty]
    private bool _filterInstalledOnly;

    /// <summary>当前是否有数据（用于空状态 UI）</summary>
    public bool HasData => CatalogItems.Count > 0;

    public PetGalleryPanelViewModel() : this(null) { }

    public PetGalleryPanelViewModel(PetManagementService? petService)
    {
        _petService = petService;
        _ = LoadAsync();
    }

    // ============ 数据加载（对齐原版 initialize：并行加载已安装/目录/激活，未激活兜底默认） ============

    public async Task LoadAsync()
    {
        if (_petService == null) return;
        try
        {
            var installed = _petService.ListInstalledPets();

            // 激活宠物：DB 读取，缺失或已卸载则兜底默认（流萤 → 任意已安装）
            _activePetId = _petService.GetActivePet();
            if (string.IsNullOrEmpty(_activePetId) ||
                installed.All(p => p.Id != _activePetId && p.FolderName != _activePetId))
            {
                _activePetId = installed.FirstOrDefault(p => p.Id == DefaultPetId || p.FolderName == DefaultPetId)?.Id
                               ?? installed.FirstOrDefault()?.Id;
                if (_activePetId != null) _petService.SetActivePet(_activePetId);
            }

            // 缩略图并行生成（首帧裁剪，后台线程；串行解码大图集是打开慢的主因之一）
            Dictionary<string, string?> thumbs;
            try
            {
                var bag = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
                await Task.Run(() => System.Threading.Tasks.Parallel.ForEach(
                    installed,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 },
                    p => bag[p.FolderName] = EnsureThumbnail(p)));
                thumbs = bag.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            catch
            {
                thumbs = new Dictionary<string, string?>();
            }

            // 第一步：立即渲染本地已安装宠物（不等网络）
            var items = installed
                .Select(p => BuildFromInstalled(p, thumbs.GetValueOrDefault(p.FolderName)))
                .ToList();
            _allItems.Clear();
            _allItems.AddRange(items);
            UpdateCounts();
            ApplyViewFilter();

            // 第二步：目录请求（未安装宠物），到达后增量合并
            var (ok, catalog, _) = await _petService.GetCatalogAsync();
            if (ok && catalog is { ValueKind: JsonValueKind.Array } arr)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var slug = GetStr(el, "slug");
                    if (string.IsNullOrEmpty(slug)) continue;
                    var inst = installed.FirstOrDefault(p => p.Id == slug || p.FolderName == slug);
                    if (inst != null) continue; // 已在本地列表
                    items.Add(new PetCatalogItem
                    {
                        Slug = slug,
                        Name = GetStr(el, "name") ?? slug,
                        DisplayName = GetLocalized(el) ?? slug,
                        Author = GetStr(el, "author") ?? GetStr(el, "author_handle") ?? "—",
                        SpriteVersionNumber = GetInt(el, "spriteVersionNumber", 1),
                        IsInstalled = false,
                        IsActive = IsSlugActive(slug),
                        ThumbnailPath = null
                    });
                }
                StatusMessage = "";
                _allItems.Clear();
                _allItems.AddRange(items);
                UpdateCounts();
                ApplyViewFilter();
            }
            else
            {
                StatusMessage = "在线目录加载失败，仅显示已安装宠物";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PetGallery] 加载宠物画廊失败");
            StatusMessage = "加载宠物目录失败：" + ex.Message;
        }
    }

    private bool IsSlugActive(string slug) => slug == _activePetId;

    private PetCatalogItem BuildFromInstalled(InstalledPetInfo inst, string? thumb) => new()
    {
        Slug = inst.Id,
        Name = inst.FolderName,
        DisplayName = string.IsNullOrEmpty(inst.DisplayName) ? inst.FolderName : inst.DisplayName,
        Author = "本地",
        SpriteVersionNumber = inst.SpriteVersionNumber,
        IsInstalled = true,
        IsActive = inst.Id == _activePetId || inst.FolderName == _activePetId,
        ThumbnailPath = thumb
    };

    // ============ 筛选（对齐原版 filteredPets：installedOnly + 名称/作者搜索） ============

    partial void OnSearchKeywordChanged(string value) => ApplyViewFilter();

    partial void OnFilterInstalledOnlyChanged(bool value) => ApplyViewFilter();

    private void ApplyViewFilter()
    {
        var list = _allItems.AsEnumerable();
        if (FilterInstalledOnly)
            list = list.Where(p => p.IsInstalled);
        var kw = SearchKeyword?.Trim();
        if (!string.IsNullOrEmpty(kw))
        {
            list = list.Where(p =>
                p.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.Slug.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (p.Author?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        CatalogItems.Clear();
        foreach (var p in list)
            CatalogItems.Add(p);
        OnPropertyChanged(nameof(HasData));
        // 视图变化（加载/搜索/筛选）后：为可视窗口内前 _thumbBudget 个未加载项拉缩略图
        EnsureThumbnailsForWindow();
    }

    private void UpdateCounts()
    {
        InstalledCount = _allItems.Count(p => p.IsInstalled);
        TotalCount = _allItems.Count;
        ActivePetName = _allItems.FirstOrDefault(p => p.IsActive)?.DisplayName;
    }

    // ============ 命令 ============

    [RelayCommand]
    private void FilterAll() => FilterInstalledOnly = false;

    [RelayCommand]
    private void FilterInstalled() => FilterInstalledOnly = true;

    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        StatusMessage = "正在刷新目录...";
        _thumbQueued.Clear(); // 手动刷新 = 重试之前下载失败的缩略图
        await LoadAsync();
        if (string.IsNullOrEmpty(StatusMessage)) StatusMessage = "目录已刷新";
    }

    [RelayCommand]
    private void SelectItem(PetCatalogItem item) => SelectedItem = item;

    [RelayCommand]
    private async Task InstallAsync(PetCatalogItem item)
    {
        if (_petService == null || IsInstalling) return;
        IsInstalling = true;
        StatusMessage = $"正在安装 {item.DisplayName}...";
        try
        {
            var (ok, info, error) = await _petService.InstallPetAsync(item.Slug);
            if (ok)
            {
                StatusMessage = $"已安装：{item.DisplayName}";
                await LoadAsync();
            }
            else
            {
                StatusMessage = $"安装失败：{error ?? "未知错误"}";
            }
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(PetCatalogItem item)
    {
        if (_petService == null) return;
        if (item.IsActive)
        {
            StatusMessage = "请先切换到其他外观再卸载";
            return;
        }
        var answer = MessageBox.Show(
            $"确认卸载 \"{item.DisplayName}\"？这会删除本地的精灵图文件。",
            "卸载确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        var (ok, _, error) = _petService.UninstallPet(item.Slug);
        StatusMessage = ok ? $"已卸载：{item.DisplayName}" : $"卸载失败：{error ?? "未知错误"}";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ActivateAsync(PetCatalogItem item)
    {
        if (_petService == null) return;
        if (!item.IsInstalled)
        {
            StatusMessage = "请先安装该宠物";
            return;
        }

        if (!_petService.SetActivePet(item.Slug))
        {
            StatusMessage = "激活失败：无法保存设置";
            return;
        }
        _activePetId = item.Slug;
        ApplyToPetWindow(item.Slug, item.SpriteVersionNumber);
        await LoadAsync();
        StatusMessage = $"已切换到：{item.DisplayName}";
    }

    /// <summary>切回默认宠物（原版 deactivatePet：切到流萤而非置空）</summary>
    [RelayCommand]
    private async Task DeactivateAsync()
    {
        if (_petService == null) return;
        if (!_petService.SetActivePet(DefaultPetId))
        {
            StatusMessage = "切换失败：无法保存设置";
            return;
        }
        _activePetId = DefaultPetId;
        var target = _allItems.FirstOrDefault(p => p.Slug == DefaultPetId);
        ApplyToPetWindow(DefaultPetId, target?.SpriteVersionNumber ?? 3);
        await LoadAsync();
        StatusMessage = "已切回默认宠物（流萤）";
    }

    [RelayCommand]
    private void Search() => ApplyViewFilter();

    /// <summary>让桌宠窗口立即切换精灵（对应原版跨窗口 onActivePetUpdated 广播）</summary>
    private static void ApplyToPetWindow(string petId, int spriteVersion)
    {
        var win = Application.Current?.Windows.OfType<Views.Pet.PetWindow>().FirstOrDefault();
        win?.ApplyPetAppearance(petId, spriteVersion);
    }

    // ============ 在线预览图：未安装宠物（对齐原版 thumbnail.webp → png → GitHub raw 降级链） ============
    // 懒加载分批：打开只拉当前视图前 30 个，滚动到底部附近再取下一批（RequestMoreThumbnails）

    /// <summary>为当前视图前 _thumbBudget 个未加载缩略图的未安装宠物排队下载（fire-and-forget）。</summary>
    private void EnsureThumbnailsForWindow()
    {
        if (_petService == null) return;
        // CatalogItems 读取须在 UI 线程（ApplyViewFilter / RequestMoreThumbnails 均为 UI 线程调用）
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(EnsureThumbnailsForWindow);
            return;
        }
        var pending = CatalogItems
            .Where(i => !i.IsInstalled && string.IsNullOrEmpty(i.ThumbnailPath)
                        && !_thumbQueued.Contains(i.Slug))
            .Take(_thumbBudget)
            .ToList();
        if (pending.Count == 0) return;
        foreach (var p in pending) _thumbQueued.Add(p.Slug);
        _ = LoadRemoteThumbnailsAsync(pending);
    }

    /// <summary>滚动接近底部时扩一批（+30）缩略图下载授权（GalleryPanel.ScrollChanged 调用）。</summary>
    public void RequestMoreThumbnails()
    {
        if (_thumbBudget >= _allItems.Count) return; // 全量已授权
        _thumbBudget += ThumbBatchSize;
        EnsureThumbnailsForWindow();
    }

    private static readonly System.Net.Http.HttpClient ThumbClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static async Task LoadRemoteThumbnailsAsync(List<PetCatalogItem> missing)
    {
        if (missing.Count == 0) return;
        var sem = new System.Threading.SemaphoreSlim(4);
        var tasks = missing.Select(async item =>
        {
            await sem.WaitAsync();
            try
            {
                var path = await FetchRemoteThumbnailAsync(item.Slug);
                if (path != null)
                    await Application.Current.Dispatcher.InvokeAsync(() => item.ThumbnailPath = path);
            }
            catch { /* 单个失败静默：卡片保持占位底色 */ }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>下载在线预览图并缓存为 PNG（.cache/{slug}.png）；三级降级：thumbnail.webp → thumbnail.png → GitHub raw 精灵图首帧</summary>
    private static async Task<string?> FetchRemoteThumbnailAsync(string slug)
    {
        var cacheDir = Path.Combine(App.DataDir, "pets", ".cache");
        Directory.CreateDirectory(cacheDir);
        var local = Path.Combine(cacheDir, slug + "-v4.png"); // v4: 在线预览图完整保留（旧版误裁为局部），需重新生成
        if (File.Exists(local)) return local;

        var urls = new[]
        {
            $"https://codexpet.top/assets/previews/{Uri.EscapeDataString(slug)}/thumbnail.webp",
            $"https://codexpet.top/assets/previews/{Uri.EscapeDataString(slug)}/thumbnail.png",
            $"https://raw.githubusercontent.com/legeling/awesome-codex-pet/main/pets/{Uri.EscapeDataString(slug)}/spritesheet.webp"
        };
        foreach (var url in urls)
        {
            try
            {
                var bytes = await ThumbClient.GetByteArrayAsync(url);
                var src = DecodeBitmap(bytes); // 魔数判断格式（远程 .webp 也可能是 PNG 内容）
                if (src == null) continue;
                var png = EncodeThumbnailPng(src);
                if (png == null) continue;
                await File.WriteAllBytesAsync(local, png);
                return local;
            }
            catch { /* 试下一级 */ }
        }
        return null;
    }

    /// <summary>
    /// 缩略图统一后处理（对齐原版 object-fit:contain 完整显示）：
    /// 仅当图片符合图集网格（宽为 192 整数倍≥768 且高为 208 整数倍）才裁左上首帧；
    /// codexpet.top 在线预览图是完整立绘，原样保留交由 Image contain 完整呈现。
    /// </summary>
    private static byte[]? EncodeThumbnailPng(BitmapSource src)
    {
        bool isSheet = src.PixelWidth >= 768
                       && src.PixelWidth % 192 == 0
                       && src.PixelHeight % 208 == 0;
        BitmapSource output = isSheet
            ? new CroppedBitmap(src, new System.Windows.Int32Rect(0, 0, 192, 208))
            : src;

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(output));
        using var ms2 = new MemoryStream();
        enc.Save(ms2);
        return ms2.ToArray();
    }

    // ============ 缩略图：精灵图首帧（192x208）裁剪为 thumb.png 缓存 ============

    /// <summary>
    /// 按文件头魔数解码位图（不信任扩展名）：
    /// awesome-codex-pet 的 .webp 实为 PNG 内容（89 50 4E 47），按扩展名走 WebP 解码必炸。
    /// PNG 头 → WPF 原生解码；RIFF/WEBP 头 → Imazen.WebP。
    /// </summary>
    private static BitmapSource? DecodeBitmap(byte[] bytes)
    {
        if (bytes.Length < 12) return null;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        // WebP: RIFF....WEBP
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            var pixels = Imazen.WebP.WebPDecoder.Decode(bytes, out var w, out var h, Imazen.WebP.WebPPixelFormat.Bgra);
            var src = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
            src.Freeze();
            return src;
        }
        return null;
    }

    private static string? EnsureThumbnail(InstalledPetInfo pet)
    {
        try
        {
            var dir = Path.Combine(App.DataDir, "pets", pet.FolderName);
            var thumbPath = Path.Combine(dir, "thumb.png");
            if (File.Exists(thumbPath)) return thumbPath;

            // 与 PetSpriteControl 一致：优先 png，回退 webp；解码按魔数（.webp 可能是 PNG 内容）
            BitmapSource? src = null;
            var png = Path.Combine(dir, "spritesheet.png");
            var webp = Path.Combine(dir, "spritesheet.webp");
            if (File.Exists(png))
                src = DecodeBitmap(File.ReadAllBytes(png));
            src ??= File.Exists(webp) ? DecodeBitmap(File.ReadAllBytes(webp)) : null;
            if (src == null) return null;

            var crop = new CroppedBitmap(src, new System.Windows.Int32Rect(0, 0,
                Math.Min(192, src.PixelWidth), Math.Min(208, src.PixelHeight)));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(crop));
            using var fs = File.Create(thumbPath);
            enc.Save(fs);
            return thumbPath;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PetGallery] 生成缩略图失败: {Pet}", pet.FolderName);
            return null;
        }
    }

    // ============ JsonElement 辅助 ============

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static string? GetLocalized(JsonElement el)
    {
        if (!el.TryGetProperty("localized_names", out var ln)) return null;
        if (ln.TryGetProperty("zh", out var zh) && zh.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(zh.GetString()))
            return zh.GetString();
        if (ln.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(en.GetString()))
            return en.GetString();
        return null;
    }
}
