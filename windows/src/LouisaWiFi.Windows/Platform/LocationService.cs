using System.Runtime.InteropServices;
using Windows.Devices.Geolocation;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace LouisaWiFi.Windows.Platform;

public sealed record LocationFix(double Latitude, double Longitude, double Accuracy, DateTimeOffset Timestamp)
{
    public bool IsUsable(DateTimeOffset now) =>
        double.IsFinite(Latitude) && Latitude is >= -90 and <= 90 &&
        double.IsFinite(Longitude) && Longitude is >= -180 and <= 180 &&
        // Exactly 500 m is Windows' coarse single-AP match; a phone hotspot's stale DB entry lands here, so reject it.
        double.IsFinite(Accuracy) && Accuracy is >= 0 and < 500 &&
        Timestamp <= now && now - Timestamp <= TimeSpan.FromMinutes(5);
}

public sealed record LocationResult(LocationFix? Fix, string? UnavailableReason);

public interface ILocationProvider
{
    Task<LocationResult> GetLocationAsync(CancellationToken cancellationToken);
}

public enum LocationPermission
{
    Allowed,
    NeedsPrompt,
    DeniedByUser,
    DeniedBySystem,
    NotAvailable
}

public sealed class LocationService : ILocationProvider, IDisposable
{
    private readonly Geolocator locator = new() { DesiredAccuracyInMeters = 100, MovementThreshold = 100 };
    private readonly AppCapability locationCapability = AppCapability.Create("location");
    private readonly AppCapability wifiCapability = AppCapability.Create("wiFiControl");
    private readonly object gate = new();
    private LocationFix? lastFix;
    private bool watching;

    public LocationService()
    {
        locationCapability.AccessChanged += OnAccessChanged;
        wifiCapability.AccessChanged += OnAccessChanged;
    }

    public event Action? Changed;

    /// Cached position for the menu's distances; null when missing, stale or too coarse.
    public LocationFix? LastUsableFix
    {
        get { lock (gate) return lastFix?.IsUsable(DateTimeOffset.UtcNow) == true ? lastFix : null; }
    }

    public LocationPermission Permission
    {
        get
        {
            var location = locationCapability.CheckAccess();
            var wifi = wifiCapability.CheckAccess();
            if (location == AppCapabilityAccessStatus.DeniedBySystem ||
                wifi == AppCapabilityAccessStatus.DeniedBySystem) return LocationPermission.DeniedBySystem;
            if (location == AppCapabilityAccessStatus.DeniedByUser ||
                wifi == AppCapabilityAccessStatus.DeniedByUser) return LocationPermission.DeniedByUser;
            if (location == AppCapabilityAccessStatus.UserPromptRequired ||
                wifi == AppCapabilityAccessStatus.UserPromptRequired) return LocationPermission.NeedsPrompt;
            if (location != AppCapabilityAccessStatus.Allowed ||
                wifi != AppCapabilityAccessStatus.Allowed) return LocationPermission.NotAvailable;
            return LocationPermission.Allowed;
        }
    }

    public async Task<LocationPermission> RequestAccessAsync()
    {
        var location = await Geolocator.RequestAccessAsync();
        if (location != GeolocationAccessStatus.Allowed)
            return Permission == LocationPermission.Allowed ? LocationPermission.NotAvailable : Permission;
        if (wifiCapability.CheckAccess() == AppCapabilityAccessStatus.UserPromptRequired)
            await wifiCapability.RequestAccessAsync();
        return Permission;
    }

    public void SetWatching(bool enabled)
    {
        if (watching == enabled) return;
        watching = enabled;
        if (enabled)
        {
            locator.PositionChanged += OnPositionChanged;
            locator.StatusChanged += OnStatusChanged;
        }
        else
        {
            locator.PositionChanged -= OnPositionChanged;
            locator.StatusChanged -= OnStatusChanged;
            lock (gate) lastFix = null;
        }
    }

    public async Task<LocationResult> GetLocationAsync(CancellationToken cancellationToken)
    {
        if (locationCapability.CheckAccess() != AppCapabilityAccessStatus.Allowed)
            return new LocationResult(null, "Windows 尚未允許定位，請使用已記住的基地台或手動選店。");

        lock (gate)
        {
            if (lastFix?.IsUsable(DateTimeOffset.UtcNow) == true)
                return new LocationResult(lastFix, null);
        }

        try
        {
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(8))
                .AsTask(cancellationToken);
            if (locationCapability.CheckAccess() != AppCapabilityAccessStatus.Allowed)
                return new LocationResult(null, "Windows 尚未允許定位，請使用已記住的基地台或手動選店。");
            var fix = ToFix(position);
            lock (gate) lastFix = fix;
            return fix.IsUsable(DateTimeOffset.UtcNow)
                ? new LocationResult(fix, null)
                : new LocationResult(null, "定位已過期或誤差達 500 公尺以上，請手動選店。");
        }
        catch (UnauthorizedAccessException)
        {
            return new LocationResult(null, "Windows 尚未允許定位，請使用已記住的基地台或手動選店。");
        }
        catch (TimeoutException)
        {
            return new LocationResult(null, "目前無法取得定位，請使用已記住的基地台或手動選店。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LocationResult(null, "目前無法取得定位，請使用已記住的基地台或手動選店。");
        }
        catch (COMException error) when ((uint)error.HResult == 0x80070005)
        {
            return new LocationResult(null, "Windows 尚未允許定位，請使用已記住的基地台或手動選店。");
        }
        catch (COMException error) when ((uint)error.HResult is 0x80004004 or 0x800705B4 or 0x80070490)
        {
            return new LocationResult(null, "目前無法取得定位，請使用已記住的基地台或手動選店。");
        }
    }

    private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args)
    {
        lock (gate) lastFix = ToFix(args.Position);
        Changed?.Invoke();
    }

    private void OnStatusChanged(Geolocator sender, StatusChangedEventArgs args) => Changed?.Invoke();

    private void OnAccessChanged(AppCapability sender, AppCapabilityAccessChangedEventArgs args)
    {
        lock (gate) lastFix = null;
        Changed?.Invoke();
    }

    private static LocationFix ToFix(Geoposition position)
    {
        var coordinate = position.Coordinate;
        var point = coordinate.Point.Position;
        return new LocationFix(point.Latitude, point.Longitude, coordinate.Accuracy, coordinate.Timestamp);
    }

    public void Dispose()
    {
        SetWatching(false);
        locationCapability.AccessChanged -= OnAccessChanged;
        wifiCapability.AccessChanged -= OnAccessChanged;
    }
}
