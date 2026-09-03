using Microsoft.AspNetCore.DataProtection;

namespace ALDevToolbox.Startup;

/// <summary>The Data Protection key ring, persisted on the app-keys volume.</summary>
public static class DataProtectionRegistration
{
    /// <summary>Registers the key ring and persists it when the key directory is writable.</summary>
    public static IServiceCollection AddDataProtectionKeyRing(this IServiceCollection services)
    {
        // Data Protection key ring. Persisted under DATA_PROTECTION_KEY_DIR
        // (compose mounts the `app-keys` volume there) so cookie auth and the
        // system_settings SMTP password ciphertext both survive container
        // restarts. If the directory isn't writable we keep going with an
        // in-memory key ring rather than crashing — operators see the warning
        // in the startup logs and can fix the volume mount.
        var dpKeyDir = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEY_DIR")
            ?? "/var/lib/aldevtoolbox/dp-keys";
        var dataProtection = services.AddDataProtection().SetApplicationName("ALDevToolbox");
        try
        {
            Directory.CreateDirectory(dpKeyDir);
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dpKeyDir));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Surfaces in startup logs; the cookie ring still works (in-memory),
            // but cookies and SMTP ciphertext won't survive a restart. Operators
            // see this immediately and can fix the volume mount.
            Console.Error.WriteLine($"WARN: Data Protection key dir '{dpKeyDir}' not writable ({ex.Message}). Keys will not persist.");
        }
        return services;
    }
}
