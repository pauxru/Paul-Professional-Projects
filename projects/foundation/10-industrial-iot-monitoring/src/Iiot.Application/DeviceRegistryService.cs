using Iiot.Domain;

namespace Iiot.Application;

public sealed record ProvisionedDevice(DeviceDescriptor Device, string DeviceKey, string EnrollmentToken);

public sealed class DeviceRegistryService(
    IDeviceRegistryStore store,
    IDeviceKeyProtector keyProtector,
    IClock clock)
{
    public async Task<ProvisionedDevice> ProvisionAsync(
        DeviceRegistration registration,
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        ValidateRegistration(registration, deviceKey);
        if (await store.DeviceExistsAsync(registration.DeviceId, cancellationToken))
        {
            throw new DomainRuleViolation($"Device '{registration.DeviceId}' is already registered.");
        }

        var descriptor = new DeviceDescriptor(
            registration.DeviceId,
            registration.DeviceType,
            registration.Plant,
            registration.Line,
            registration.Asset,
            registration.FirmwareVersion,
            DeviceStatus.Provisioned,
            registration.CertificateThumbprint,
            false,
            clock.UtcNow);
        var credential = new DeviceCredential(
            registration.DeviceId,
            keyProtector.Hash(deviceKey),
            keyProtector.Hash(registration.EnrollmentToken),
            registration.CertificateThumbprint,
            false);
        await store.CreateDeviceAsync(descriptor, credential, new DeviceTwin().Snapshot(), cancellationToken);
        return new ProvisionedDevice(descriptor, deviceKey, registration.EnrollmentToken);
    }

    public async Task<DeviceDescriptor> EnrollAsync(
        string deviceId,
        string enrollmentToken,
        CancellationToken cancellationToken = default)
    {
        var credential = await store.FindCredentialAsync(deviceId, cancellationToken);
        var device = await store.FindDeviceAsync(deviceId, cancellationToken);
        if (credential is null || device is null || credential.IsRevoked || !keyProtector.Verify(enrollmentToken, credential.EnrollmentTokenHash))
        {
            throw new DomainRuleViolation("Enrollment token is invalid or the device has been revoked.");
        }

        await store.UpdateDeviceStatusAsync(deviceId, DeviceStatus.Offline, cancellationToken);
        return device with { Status = DeviceStatus.Offline };
    }

    public async Task<bool> AuthenticateDeviceAsync(string deviceId, string deviceKey, CancellationToken cancellationToken = default)
    {
        var credential = await store.FindCredentialAsync(deviceId, cancellationToken);
        return credential is not null
            && !credential.IsRevoked
            && keyProtector.Verify(deviceKey, credential.KeyHash);
    }

    public async Task RevokeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await store.FindDeviceAsync(deviceId, cancellationToken)
            ?? throw new DomainRuleViolation($"Device '{deviceId}' does not exist.");
        await store.UpdateDeviceStatusAsync(device.DeviceId, DeviceStatus.Revoked, cancellationToken);
    }

    public async Task<DeviceTwinSnapshot> PatchTwinAsync(
        string deviceId,
        IReadOnlyDictionary<string, string?> patch,
        int expectedVersion,
        bool reported,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await store.GetTwinAsync(deviceId, cancellationToken)
            ?? throw new DomainRuleViolation($"Device '{deviceId}' does not exist.");
        var twin = DeviceTwin.FromSnapshot(snapshot);
        if (reported)
        {
            twin.PatchReported(patch, expectedVersion);
        }
        else
        {
            twin.PatchDesired(patch, expectedVersion);
        }

        var updated = twin.Snapshot();
        await store.SaveTwinAsync(deviceId, updated, cancellationToken);
        return updated;
    }

    private static void ValidateRegistration(DeviceRegistration registration, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(registration.DeviceId) ||
            string.IsNullOrWhiteSpace(registration.DeviceType) ||
            string.IsNullOrWhiteSpace(registration.Plant) ||
            string.IsNullOrWhiteSpace(registration.Line) ||
            string.IsNullOrWhiteSpace(registration.Asset) ||
            string.IsNullOrWhiteSpace(registration.FirmwareVersion) ||
            string.IsNullOrWhiteSpace(registration.EnrollmentToken) ||
            deviceKey.Length < 12)
        {
            throw new DomainRuleViolation("Device registration requires hierarchy, firmware, enrollment token, and a 12-character device key.");
        }
    }
}
