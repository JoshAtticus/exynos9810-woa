// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Device;

/// <summary>What the installer has been validated against. Anything else is refused or warned.</summary>
public sealed record SupportedTarget(string Model, string ModelCode, string Codename, string HardwareToken,
    string ValidatedBootloader)
{
    public static SupportedTarget GalaxyS9Plus { get; } =
        new("SM-G965F", "G965F", "star2lte", "exynos9810", "G965FXXUHFVG4");

    public static SupportedTarget GalaxyS9 { get; } =
        new("SM-G960F", "G960F", "starlte", "exynos9810", "G960FXXUHFVG6");

    public static IReadOnlyList<SupportedTarget> All { get; } = [GalaxyS9Plus, GalaxyS9];
}

public static class DeviceEligibility
{
    public static IReadOnlyList<CheckResult> Evaluate(DeviceSnapshot d, IReadOnlyList<SupportedTarget>? targets = null)
    {
        targets ??= SupportedTarget.All;
        var results = new List<CheckResult>();

        if (d.Mode == DeviceMode.Unauthorized)
        {
            results.Add(new("usb-auth", "USB debugging authorization", CheckSeverity.Blocker,
                "The phone has not authorized this PC.",
                "Unlock the phone and tap Allow on the \"Allow USB debugging?\" prompt."));
            return results;
        }
        if (d.Mode is DeviceMode.Offline or DeviceMode.Unknown)
        {
            results.Add(new("usb-state", "Connection", CheckSeverity.Blocker,
                "The phone is connected but not responding over ADB.",
                "Reconnect the USB cable, or restart the phone."));
            return results;
        }

        var target = targets.FirstOrDefault(t => string.Equals(t.Model, d.Model, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            var isS9Family = d.Model is not null && (d.Model.Contains("G960", StringComparison.OrdinalIgnoreCase)
                || d.Model.Contains("G965", StringComparison.OrdinalIgnoreCase));
            results.Add(new("model", "Phone model", CheckSeverity.Blocker,
                $"{d.Model ?? "Unknown model"} is not supported.",
                isS9Family
                    ? "Only the Exynos Galaxy S9 (SM-G960F) and S9+ (SM-G965F) are supported today. Snapdragon models are not."
                    : "This installer only supports the Exynos Samsung Galaxy S9 (SM-G960F) and S9+ (SM-G965F)."));
            return results;
        }
        results.Add(new("model", "Phone model", CheckSeverity.Pass, $"{target.Model} ({target.Codename})"));

        if (d.Hardware is not null && !d.Hardware.Contains(target.HardwareToken, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new("soc", "Chipset", CheckSeverity.Blocker,
                $"Reported hardware \"{d.Hardware}\" is not Exynos 9810."));
        }
        else
        {
            results.Add(new("soc", "Chipset", CheckSeverity.Pass, "Exynos 9810"));
        }

        var validated = SamsungBuild.TryParse(target.ValidatedBootloader, target.ModelCode)!;
        var build = SamsungBuild.TryParse(d.Bootloader, target.ModelCode);
        if (build is null)
        {
            results.Add(new("bootloader", "Firmware version", CheckSeverity.Warning,
                $"Could not read the bootloader version ({d.Bootloader ?? "not reported"}).",
                $"The installer is validated on {target.ValidatedBootloader}."));
        }
        else if (build.Raw == validated.Raw)
        {
            results.Add(new("bootloader", "Firmware version", CheckSeverity.Pass, $"{build.Raw} (validated)"));
        }
        else if (build.BinaryRevision < validated.BinaryRevision || build.ReleaseKey.CompareTo(validated.ReleaseKey) < 0)
        {
            results.Add(new("bootloader", "Firmware version", CheckSeverity.Blocker,
                $"{build.Raw} is older than the validated firmware {validated.Raw}.",
                "Update the phone to the latest official Samsung firmware (Settings > Software update) before installing."));
        }
        else
        {
            results.Add(new("bootloader", "Firmware version", CheckSeverity.Warning,
                $"{build.Raw} differs from the validated firmware {validated.Raw}.",
                "Installation may work but has not been tested on this firmware."));
        }

        if (d.FlashLocked == false)
        {
            results.Add(new("unlock", "Bootloader", CheckSeverity.Pass, "Unlocked"));
        }
        else if (d.Mode == DeviceMode.Android && d.OemUnlockAllowed == true)
        {
            results.Add(new("unlock", "Bootloader", CheckSeverity.Info, "Locked; OEM unlocking is enabled.",
                "The installer will guide you through unlocking in Download mode. This erases the phone."));
        }
        else if (d.Mode == DeviceMode.Android)
        {
            results.Add(new("unlock", "Bootloader", CheckSeverity.Blocker, "Locked; OEM unlocking is not enabled.",
                "Enable Developer options, then turn on Settings > Developer options > OEM unlocking. "
                + "If the switch is missing, connect to the internet, check for software updates, and wait (the phone may need to be online for several days)."));
        }
        else if (d.Mode == DeviceMode.Download)
        {
            results.Add(new("unlock", "Bootloader", CheckSeverity.Info, "Not reported in Download mode.",
                "The phone refuses TWRP if the bootloader is still locked."));
        }
        else
        {
            results.Add(new("unlock", "Bootloader", CheckSeverity.Warning, "Lock state not reported by recovery."));
        }

        results.Add(d.WarrantyTripped switch
        {
            true => new("knox", "Knox warranty bit", CheckSeverity.Info, "Already tripped (0x1)."),
            null when d.Mode == DeviceMode.Download => new("knox", "Knox warranty bit", CheckSeverity.Info, "Not reported in Download mode."),
            _ => new("knox", "Knox warranty bit", CheckSeverity.Warning, "Intact. Installing will permanently trip it.",
                "Samsung Pay, Secure Folder and Knox-based features stop working permanently."),
        });

        results.Add(d.Mode switch
        {
            DeviceMode.Recovery => new("mode", "Current mode", CheckSeverity.Info, $"Recovery{(d.RecoveryVersion is null ? "" : $" (TWRP {d.RecoveryVersion})")}"),
            DeviceMode.Download => new("mode", "Current mode", CheckSeverity.Info, "Download mode (the phone identified earlier)"),
            _ => new("mode", "Current mode", CheckSeverity.Info, $"Android {d.AndroidVersion}"),
        });

        return results;
    }
}
