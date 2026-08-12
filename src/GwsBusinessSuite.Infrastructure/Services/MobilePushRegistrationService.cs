using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Mobile;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class MobilePushRegistrationService(IAppDbContext db, TimeProvider timeProvider) : IMobilePushRegistrationService
{
    public async Task<MobileDeviceView> RegisterDeviceAsync(
        string username, string platform, string pushToken, string deviceName, CancellationToken cancellationToken = default)
    {
        if (!MobileDevicePlatforms.All.Contains(platform, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown platform '{platform}'.", nameof(platform));
        }
        if (string.IsNullOrWhiteSpace(pushToken))
        {
            throw new ArgumentException("A push token is required.", nameof(pushToken));
        }

        var now = timeProvider.GetUtcNow();
        // Re-registering the same token (e.g. app relaunch) updates the existing row rather than
        // accumulating duplicates - a device's token is stable across most of its lifetime.
        var device = await db.MobileDeviceRegistrations
            .FirstOrDefaultAsync(item => item.Username == username && item.PushToken == pushToken, cancellationToken);
        if (device is null)
        {
            device = new MobileDeviceRegistration
            {
                Username = username,
                Platform = platform,
                PushToken = pushToken,
                DeviceName = deviceName,
                RegisteredAt = now,
                CreatedAt = now,
                CreatedBy = username
            };
            await db.MobileDeviceRegistrations.AddAsync(device, cancellationToken);
        }
        device.DeviceName = deviceName;
        device.LastSeenAt = now;
        device.UpdatedAt = now;
        device.UpdatedBy = username;
        await db.SaveChangesAsync(cancellationToken);

        return new MobileDeviceView(device.Id, device.Platform, device.DeviceName, device.RegisteredAt, device.LastSeenAt);
    }

    public async Task UnregisterDeviceAsync(string username, string pushToken, CancellationToken cancellationToken = default)
    {
        var device = await db.MobileDeviceRegistrations
            .FirstOrDefaultAsync(item => item.Username == username && item.PushToken == pushToken, cancellationToken);
        if (device is null) return;
        db.MobileDeviceRegistrations.Remove(device);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MobileDeviceView>> ListDevicesForUserAsync(string username, CancellationToken cancellationToken = default)
    {
        var devices = await db.MobileDeviceRegistrations.AsNoTracking()
            .Where(item => item.Username == username)
            .Select(item => new { item.Id, item.Platform, item.DeviceName, item.RegisteredAt, item.LastSeenAt })
            .ToListAsync(cancellationToken);
        return devices.Select(item => new MobileDeviceView(item.Id, item.Platform, item.DeviceName, item.RegisteredAt, item.LastSeenAt)).ToList();
    }
}
