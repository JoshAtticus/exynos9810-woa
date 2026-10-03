// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// The validated Exynos Galaxy S9/S9+ partition layout the installer relies on.
/// PIT-verified identical on starlte (SM-G960F, STARLTE_EUR_OPEN.pit) and star2lte:
/// same names, same block counts, same USERDATA offset. Names are the stable
/// <c>/dev/block/by-name</c> links (upper case on this phone; lookups are
/// case-insensitive), independent of the sd* node numbering.
/// </summary>
public static class PartitionMap
{
    /// <summary>
    /// Identity- and calibration-critical partitions to back up before anything is
    /// written. <c>EFS</c> holds the IMEI, Wi-Fi/Bluetooth MACs and serial on Exynos
    /// and is irreplaceable; the rest are backed up when present. Missing names are skipped.
    /// </summary>
    public static IReadOnlyList<string> IdentityBackup { get; } =
    [
        "EFS", "CPEFS", "PARAM", "UP_PARAM", "STEADY", "KEYSTORAGE", "PERSISTENT",
    ];

    /// <summary>A backup is only trustworthy if at least these partitions were captured.</summary>
    public static IReadOnlyList<string> RequiredBackup { get; } = ["EFS"];

    /// <summary>Partition that holds the UEFI boot image. RECOVERY keeps TWRP.</summary>
    public const string UefiTarget = "BOOT";

    /// <summary>Partition Windows is written to on this device.</summary>
    public const string WindowsTarget = "USERDATA";

    /// <summary>Partition TWRP is flashed to from Download mode.</summary>
    public const string RecoveryTarget = "RECOVERY";

    /// <summary>
    /// The EFI system partition the UEFI boots from: Android's CACHE (sda21, 600 MiB). It is
    /// reformatted as FAT32 with 4096-byte sectors (the UFS block size) and holds
    /// \EFI\Microsoft\Boot\bootmgfw.efi and the BCD, whose boot-manager device points here.
    /// </summary>
    public const string EfiSystemPartition = "CACHE";

    /// <summary>
    /// Second FAT32 copy of the boot files on Android's SYSTEM partition (sda18). The reference
    /// deployment kept both; firmware builds that only connect the SYSTEM-sized FAT volume use it.
    /// On the reference phone its GPT type is the EFI system partition GUID (a stock flash resets it
    /// to basic data); <see cref="GptTypeService"/> puts that back.
    /// </summary>
    public const string SecondaryEfiSystemPartition = "SYSTEM";

    /// <summary>
    /// Holds the Android bootloader control block. <c>boot-recovery</c> there makes the bootloader
    /// start RECOVERY; the UEFI writes it too when it gives up on Windows.
    /// </summary>
    public const string Misc = "MISC";

    /// <summary>
    /// Samsung derives GPT identifiers from names ("ANDROID MMC DISK", "ANDROID USERDATA", ...),
    /// so they are the same on every starlte/star2lte. The BCD addresses Windows by them.
    /// </summary>
    public const string DiskGuid = "{52444e41-494f-2044-4d4d-43204449534b}";
    public const string UserdataGuid = "{52444e41-494f-2044-5553-455244415441}";
    public const string CacheGuid = "{52444e41-494f-2044-4341-434845000000}";
    public const string SystemGuid = "{52444e41-494f-2044-5359-5354454d0000}";

    /// <summary>
    /// Byte geometry of the phone's main UFS unit and of USERDATA on it. The Windows image is
    /// built on a virtual disk with exactly this layout so the NTFS volume matches USERDATA.
    /// </summary>
    public const long DiskBytes = 63_963_136_000;
    public const long WindowsOffset = 6_951_534_592;
    public const long WindowsBytes = 57_004_785_664;
    public const long CacheBytes = 629_145_600;
}
