using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Overlay.Core.Ads;

namespace Overlay.Client.Ads;

/// <summary>
/// M29 §D3: the ONE ad slot, a 728x90 leaderboard pinned to the bottom of the HOME window. The
/// space is reserved by the window layout (see HomeWindow.xaml), so an empty/failed/dormant slot
/// hides its contents without ever moving the UI above it.
///
/// <para><b>Cost control.</b> One <see cref="DispatcherTimer"/> at <see cref="DispatcherPriority.Background"/>,
/// never faster than <see cref="AdSlotService.MinRotationInterval"/>; decode happens on a thread-pool
/// thread at display size (<c>DecodePixelWidth</c>) and the bitmap is frozen before it crosses back
/// to the UI thread. No per-frame work exists anywhere in this control.</para>
///
/// <para><b>D2 dormancy.</b> Game live, window hidden, or control unloaded → timer stopped and the
/// bitmap reference dropped so the decoded pixels are collectable. Nothing here runs while a game is
/// in progress.</para>
///
/// <para><b>Failure posture.</b> Every failure path calls <see cref="Collapse"/>: contents hidden, no
/// dialog, no spinner, no retry loop (the service caps session failures).</para>
/// </summary>
public sealed class AdBanner : UserControl
{
    /// <summary>Slot size. The window reserves exactly this height plus its margins, so adding or
    /// removing a creative never reflows the views above.</summary>
    public const int SlotWidth = 728;
    public const int SlotHeight = 90;

    /// <summary>§2 creative contract: anything larger than this is rejected before decode.</summary>
    private const int MaxSourceWidth = 800;
    private const int MaxSourceHeight = 200;

    private readonly AdSlotService _service;
    private readonly Border _slot;
    private readonly Image _image;
    private readonly DispatcherTimer _timer;

    private Window? _host;
    private AdCreative? _current;
    private bool _loading;

    public AdBanner(AdSlotService service)
    {
        _service = service;

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };

        // "AD" tag: the user should always be able to tell paid placement from app content.
        var tag = new TextBlock
        {
            Text = "AD",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 6, 0),
        };

        _slot = new Border
        {
            Width = SlotWidth,
            Height = SlotHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Application.Current.FindResource("Surface"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Visibility = Visibility.Hidden, // reserved space stays; contents appear only once loaded
            Child = new Grid { Children = { _image, tag } },
        };
        _slot.MouseLeftButtonUp += OnSlotClicked;

        Height = SlotHeight;
        Content = _slot;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = AdSlotService.MinRotationInterval,
        };
        _timer.Tick += (_, _) => LoadNext();

        Loaded += OnLoaded;
        Unloaded += (_, _) => Pause();
        _service.DormancyChanged += OnDormancyChanged;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            _host = Window.GetWindow(this);
            // Minimised/hidden HOME rotates nothing — no reason to fetch what nobody sees (D2).
            if (_host is not null) _host.IsVisibleChanged += (_, _) => Sync();
        }
        Sync();
    }

    private void OnDormancyChanged() => Dispatcher.BeginInvoke(new Action(Sync), DispatcherPriority.Background);

    private void Sync()
    {
        if (_service.IsDormant || _host is { IsVisible: false }) Pause();
        else Resume();
    }

    private void Resume()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        LoadNext();
    }

    /// <summary>Stop rotating and drop the decoded bitmap so it can be reclaimed (D2: in-game RAM
    /// delta must be 0).</summary>
    private void Pause()
    {
        _timer.Stop();
        _image.Source = null;
        _current = null;
        _slot.Visibility = Visibility.Hidden;
    }

    // ── Load + decode ───────────────────────────────────────────────────────────

    private async void LoadNext()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var ad = await _service.NextAsync().ConfigureAwait(true);
            if (ad is null) { Collapse(); return; }

            var bitmap = await Task.Run(() => Decode(ad.Bytes)).ConfigureAwait(true);
            if (bitmap is null) { Collapse(); return; }

            // A game may have started while we were fetching/decoding — dormancy wins.
            if (_service.IsDormant) { Collapse(); return; }

            _current = ad.Creative;
            _image.Source = bitmap;
            _slot.Visibility = Visibility.Visible;
            _service.RecordImpression(ad.Creative);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AdBanner] load skipped: {ex.Message}");
            Collapse();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Decodes on a thread-pool thread, at slot width, and freezes so the result can cross
    /// to the UI thread. Returns null for an oversized source (§2 contract: max 800x200).</summary>
    private static BitmapImage? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);

        var probe = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (probe.PixelWidth > MaxSourceWidth || probe.PixelHeight > MaxSourceHeight) return null;

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;         // closes the stream at EndInit
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = Math.Min(SlotWidth, probe.PixelWidth); // never decode above display size
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void Collapse()
    {
        _image.Source = null;
        _current = null;
        _slot.Visibility = Visibility.Hidden;
    }

    // ── Click ───────────────────────────────────────────────────────────────────

    /// <summary>§3: a click opens the SYSTEM browser. Nothing navigates in-process, and only
    /// http/https destinations are honoured.</summary>
    private void OnSlotClicked(object sender, MouseButtonEventArgs e)
    {
        var url = _current?.Click;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AdBanner] click failed: {ex.Message}");
        }
    }

    /// <summary>Called by the host window on close: stop the timer and unhook the service event.</summary>
    public void Shutdown()
    {
        _service.DormancyChanged -= OnDormancyChanged;
        Pause();
    }
}
