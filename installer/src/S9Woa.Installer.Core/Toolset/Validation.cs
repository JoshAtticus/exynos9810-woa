// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text;

namespace S9Woa.Installer.Core.Toolset;

/// <summary>Checks that a file is an Android boot image that fits a phone partition.</summary>
public static class BootImage
{
    // PIT-verified identical on starlte (SM-G960F, STARLTE_EUR_OPEN.pit) and
    // star2lte: same block counts, so one constant covers both models.
    public const long RecoveryPartitionBytes = 16638L * 4096; // RECOVERY (65 MiB).
    public const long BootPartitionBytes = 14080L * 4096;     // BOOT (55 MiB).

    private static readonly byte[] Magic = "ANDROID!"u8.ToArray();

    /// <summary>Returns null when valid, otherwise a user-facing reason.</summary>
    public static string? Validate(string file, long maxBytes)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return "The file does not exist.";
        }
        if (info.Length < 4096)
        {
            return "The file is too small to be a boot image.";
        }
        if (info.Length > maxBytes)
        {
            return $"The image ({info.Length / (1024 * 1024)} MiB) does not fit the {maxBytes / (1024 * 1024)} MiB partition.";
        }
        Span<byte> head = stackalloc byte[8];
        using (var stream = File.OpenRead(file))
        {
            if (stream.Read(head) != head.Length || !head.SequenceEqual(Magic))
            {
                return "This is not an Android boot image (missing the ANDROID! header).";
            }
        }
        return null;
    }

    /// <summary>TWRP must be an Exynos Galaxy S9/S9+ build; official files end in -starlte.img (S9) or -star2lte.img (S9+).</summary>
    public static string? ValidateTwrp(string file)
    {
        var name = Path.GetFileName(file);
        if (!name.Contains("starlte", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("star2lte", StringComparison.OrdinalIgnoreCase))
        {
            return "This does not look like TWRP for the Exynos Galaxy S9/S9+. Official file names end in -starlte.img "
                + "(S9) or -star2lte.img (S9+), not starqlte/star2qlte (Snapdragon models).";
        }
        return Validate(file, RecoveryPartitionBytes);
    }

    public static string? ValidateUefi(string file) => Validate(file, BootPartitionBytes);

    internal static string Describe(string file) =>
        new StringBuilder(Path.GetFileName(file)).Append(" (").Append(new FileInfo(file).Length / 1024).Append(" KiB)").ToString();
}

/// <summary>
/// Detects whether the Samsung Download-mode USB interface (VID 04E8, PID 685D)
/// is bound to WinUSB/libusbK, which Heimdall needs. The device only appears
/// after the phone has been connected in Download mode at least once.
/// </summary>
public static class DownloadModeDriver
{
    public const string HardwarePrefix = "VID_04E8&PID_685D";
    private const string EnumUsb = @"SYSTEM\CurrentControlSet\Enum\USB";
    private static readonly string[] UsableServices = ["WinUSB", "libusbK", "libusb0"];

    public static ToolStatus Detect(IRegistryReader registry)
    {
        var seen = false;
        foreach (var device in registry.SubKeyNames(EnumUsb)
                     .Where(n => n.StartsWith(HardwarePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var instance in registry.SubKeyNames($@"{EnumUsb}\{device}"))
            {
                seen = true;
                var service = registry.GetString($@"{EnumUsb}\{device}\{instance}", "Service");
                if (service is not null && UsableServices.Contains(service, StringComparer.OrdinalIgnoreCase))
                {
                    return new ToolStatus(ToolState.Ready, $"Download mode uses {service}.");
                }
            }
        }
        return new ToolStatus(ToolState.Deferred, seen
            ? "The phone's Download-mode interface still uses the Samsung driver. Put the phone in Download mode, open Zadig, "
              + "choose Options > List All Devices, select the Samsung device, pick WinUSB and click Replace Driver."
            : "Done during Install TWRP: with the phone in Download mode, open Zadig and replace its driver with WinUSB.");
    }
}
