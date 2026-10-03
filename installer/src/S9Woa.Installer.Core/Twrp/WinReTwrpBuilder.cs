// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>What a candidate base image is, so callers can decide how to treat it.</summary>
public enum BaseImageKind
{
    /// <summary>An official TWRP 3.7.0_9-0 for starlte (S9) or star2lte (S9+), ready to build from.</summary>
    OfficialTwrp,

    /// <summary>Already a WinRE build (from us or the research tool); flash as-is.</summary>
    WinReBuild,

    /// <summary>Neither; refuse.</summary>
    Unknown,
}

/// <summary>The result of a successful build.</summary>
/// <param name="Gears"><c>builtin</c>, or <c>sha256:&lt;hex&gt;</c> of the user's gear GIF the frames came from.</param>
public sealed record WinReBuildResult(byte[] Image, string Sha256, string BuilderVersion, string BaseSha256, string Gears = WinReTwrpBuilder.BuiltinGears)
{
    public int Bytes => Image.Length;
}

/// <summary>The <c>twres/winre-build.txt</c> stamp a build of this installer leaves in its ramdisk.</summary>
/// <param name="Fonts"><c>windows</c> (Segoe UI from the builder's Windows) or <c>open</c> (redistributable fonts).</param>
public sealed record WinReStamp(string Builder, string BaseSha256, string Gears, string Fonts = "windows");

/// <summary>
/// Builds the WinRE-look recovery on the end user's PC from the official TWRP
/// image: it verifies the base is exactly TWRP 3.7.0_9-0 for star2lte, applies
/// the four-byte power-off route patch to the kernel, reskins the stock theme with
/// the committed reskin.xml, replaces every stock bitmap with the original art in
/// assets/stock, adds the embedded WinRE pages, icons and gears (or, when the user
/// supplies their own UpdateOS gear GIF, frames rendered from it) and Segoe fonts
/// copied from the user's own Windows, bakes in the /sbin scripts and the GPL
/// kernel modules, patches init.recovery.usb.rc so adb survives a TWRP crash,
/// registers the ntfs-3g FUSE deadlock breaker as an init service,
/// writes a builder marker, and repacks. It asserts the output fits RECOVERY and
/// that the kernel (bar the patch), device-tree and second stage are unchanged.
/// Deterministic: the same base image, fonts and GIF produce the same bytes.
///
/// This is the C# counterpart of tools/twrp-winre/build.py and shares the same
/// committed theme, scripts and art.
/// </summary>
public sealed class WinReTwrpBuilder
{
    /// <summary>
    /// winre-2: live-progress install screen, reskin.xml templates/styles, original
    /// replacements for every stock bitmap, WinRE singleaction/action pages.
    /// </summary>
    public const string BuilderVersion = "winre-2";

    /// <summary>Marker value for the procedural gears committed with the theme.</summary>
    public const string BuiltinGears = "builtin";

    /// <summary>The marker value for frames rendered from this GIF.</summary>
    public static string GearsId(byte[]? gif) => gif is null ? BuiltinGears : "sha256:" + Sha256(gif);

    /// <summary>SHA-256 of the official twrp-3.7.0_9-0-star2lte.img from twrp.me.</summary>
    public const string OfficialTwrpSha256 = "f674dab0134f3c929982077b6a3a8de7df209a45c76ead80bc009381bcd835e1";

    /// <summary>Length of that official file (its last section ends here, no partition padding).</summary>
    public const int OfficialTwrpBytes = 42_670_080;

    /// <summary>SHA-256 of the official twrp-3.7.0_9-0-starlte.img from twrp.me.</summary>
    public const string OfficialTwrpStarlteSha256 = "905f81903849a5b981fe8915c91d2ba025358f79324e2427c2b704a4f9e46a4e";

    /// <summary>Length of that official file (its last section ends here, no partition padding).</summary>
    public const int OfficialTwrpStarlteBytes = 42_665_984;

    private const int ModuleDirMode = 0x1FF; // directory 0755 handled by cpio; files 0644.
    private const string ModulePath = "sbin/s9woa";

    private static readonly string[] FontMap =
    [
        // dest ramdisk name : Windows font file
        "winre-light.ttf:segoeuil.ttf",
        "winre-semilight.ttf:segoeuisl.ttf",
        "winre-regular.ttf:segoeui.ttf",
    ];

    /// <summary>Default Windows fonts directory.</summary>
    public static string DefaultFontsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    /// <summary>
    /// The three recovery faces and where each comes from: Segoe UI (<c>segoeuil/segoeuisl/segoeui.ttf</c>)
    /// when the folder has it - the user's own Windows - or else fonts already named
    /// <c>winre-light/semilight/regular.ttf</c>, the redistributable open fonts a public build uses.
    /// </summary>
    internal static (IReadOnlyList<(string Dest, string Source)> Fonts, bool Open) ResolveFonts(string directory)
    {
        var slots = FontMap.Select(p => p.Split(':')).ToList();
        if (slots.All(s => File.Exists(Path.Combine(directory, s[1]))))
        {
            return (slots.Select(s => (s[0], Path.Combine(directory, s[1]))).ToList(), false);
        }
        if (slots.All(s => File.Exists(Path.Combine(directory, s[0]))))
        {
            return (slots.Select(s => (s[0], Path.Combine(directory, s[0]))).ToList(), true);
        }
        var missing = slots.First(s => !File.Exists(Path.Combine(directory, s[1])))[1];
        throw new InvalidOperationException(
            $"The Windows font {missing} was not found in {directory}. It is copied from your own "
            + "Windows to give the recovery the Segoe UI look; the installer cannot proceed without it.");
    }

    /// <summary>Classify a candidate .img: official TWRP, an existing WinRE build, or unknown.</summary>
    public static BaseImageKind Classify(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (MatchesOfficial(image))
        {
            return BaseImageKind.OfficialTwrp;
        }
        try
        {
            var boot = AndroidBootImage.Parse(image);
            var cpio = CpioArchive.Parse(Lzma.LzmaAlone.Decompress(boot.Ramdisk));
            if (cpio.Get("twres/winre.xml") is not null || cpio.Get("twres/winre-build.txt") is not null
                || cpio.Get("sbin/winre-statuswatch.sh") is not null || cpio.Get("sbin/winre-pushwatch.sh") is not null)
            {
                return BaseImageKind.WinReBuild;
            }
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            // Not a boot image we understand.
        }
        return BaseImageKind.Unknown;
    }

    /// <summary>
    /// The build stamp of a WinRE image built by this installer (any version), or null for anything
    /// else - e.g. the research tool's builds, which carry no stamp.
    /// </summary>
    public static WinReStamp? ReadStamp(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            var cpio = CpioArchive.Parse(Lzma.LzmaAlone.Decompress(AndroidBootImage.Parse(image).Ramdisk));
            if (cpio.Get("twres/winre-build.txt") is not { } entry)
            {
                return null;
            }
            var fields = Encoding.UTF8.GetString(entry.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
            return fields.TryGetValue("builder", out var builder) && builder.Length > 0
                ? new WinReStamp(builder, fields.GetValueOrDefault("base_sha256", ""), fields.GetValueOrDefault("gears", ""),
                    fields.GetValueOrDefault("fonts", "windows"))
                : null;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool MatchesOfficial(byte[] image) => MatchesOfficial(image, out _);

    public static bool MatchesOfficial(byte[] image, out string codename)
    {
        ArgumentNullException.ThrowIfNull(image);
        var sha = Sha256(image);
        if (sha == OfficialTwrpSha256)
        {
            codename = "star2lte";
            return true;
        }
        if (sha == OfficialTwrpStarlteSha256)
        {
            codename = "starlte";
            return true;
        }
        // Accept a partition dump padded with zeros to the RECOVERY size whose
        // leading bytes are an official image and the rest is zero.
        foreach (var (knownSha, knownBytes, name) in new[]
                 {
                     (OfficialTwrpSha256, OfficialTwrpBytes, "star2lte"),
                     (OfficialTwrpStarlteSha256, OfficialTwrpStarlteBytes, "starlte"),
                 })
        {
            if (image.Length > knownBytes
                && !image.AsSpan(knownBytes).ContainsAnyExcept((byte)0)
                && Sha256(image.AsSpan(0, knownBytes).ToArray()) == knownSha)
            {
                codename = name;
                return true;
            }
        }
        codename = "";
        return false;
    }

    private static byte[] OfficialPayload(byte[] image)
    {
        MatchesOfficial(image, out var codename);
        var knownBytes = codename == "starlte" ? OfficialTwrpStarlteBytes : OfficialTwrpBytes;
        return image.Length == knownBytes ? image : image.AsSpan(0, knownBytes).ToArray();
    }

    /// <summary>
    /// Build the WinRE recovery image. <paramref name="fontsDirectory"/> defaults
    /// to the user's Windows fonts; <paramref name="modulesDirectory"/> overrides
    /// the embedded kernel modules if supplied (else the embedded ones are used);
    /// <paramref name="gearsGif"/> is the user's own UpdateOS gear animation, whose
    /// frames then replace the procedural gears (never stored anywhere else).
    /// </summary>
    public WinReBuildResult Build(byte[] baseImage, string? fontsDirectory = null, string? modulesDirectory = null,
        IList<string>? report = null, byte[]? gearsGif = null)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        var baseSha = Sha256(baseImage);
        if (!MatchesOfficial(baseImage, out var baseCodename))
        {
            throw new InvalidOperationException(
                "This is not the official TWRP 3.7.0_9-0 for starlte/star2lte (checked by SHA-256). "
                + "Download twrp-3.7.0_9-0-starlte.img (S9) or -star2lte.img (S9+) from twrp.me and choose it on the Set up page.");
        }
        var raw = OfficialPayload(baseImage);
        fontsDirectory ??= DefaultFontsDirectory;

        var boot = AndroidBootImage.Parse(raw);
        var baseKernel = boot.Kernel;
        var baseDt = boot.Dt;
        var baseSecond = boot.Second;
        if (!boot.Serialize(refreshId: false).AsSpan().SequenceEqual(raw))
        {
            throw new InvalidOperationException("Boot image does not round-trip; refusing to build.");
        }
        if (boot.Tail.AsSpan().ContainsAnyExcept((byte)0))
        {
            throw new InvalidOperationException("Recovery partition tail is not zero fill; not safe to re-pad.");
        }

        // Power-off route patch (kernel; hash/signature gated per device: the
        // star2lte and starlte kernels compile sec_power_off differently, so
        // each has its own derived offsets in PowerOffRoutePatch).
        boot.Kernel = PowerOffRoutePatch.Patch(boot.Kernel);
        report?.Add("kernel: power-off route patched (4 bytes)");

        var plain = Lzma.LzmaAlone.Decompress(boot.Ramdisk);
        var cpio = CpioArchive.Parse(plain);
        if (!cpio.Serialize().AsSpan().SequenceEqual(plain))
        {
            throw new InvalidOperationException("cpio does not round-trip; refusing to build.");
        }

        var twres = cpio.Get("twres/ui.xml") is not null ? "twres" : "/twres";
        if (cpio.Get($"{twres}/ui.xml") is null)
        {
            throw new InvalidOperationException("No twres/ui.xml in the ramdisk.");
        }

        // Gears: the user's UpdateOS GIF if given, else the committed procedural frames.
        IReadOnlyList<byte[]> gearFrames;
        int gearFps;
        if (gearsGif is not null)
        {
            GearFrames.Result gears;
            try
            {
                gears = GearFrames.FromGif(gearsGif);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidOperationException($"The gear animation could not be read: {e.Message}", e);
            }
            gearFrames = gears.Pngs;
            gearFps = gears.Fps;
            report?.Add($"gears: {gearFrames.Count} frame(s) from your UpdateOS GIF ({gears.MeanDelayMs:0} ms/frame, fps {gearFps})");
        }
        else
        {
            gearFrames = WinReResources.Folder("images")
                .Where(kv => kv.Key.StartsWith("winrecog", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value)
                .ToList();
            gearFps = 24;
            report?.Add($"gears: {gearFrames.Count} built-in frame(s)");
        }

        var vars = WinReTheme.BuildVariables(gearFps);
        var icons = WinReResources.IconNames();
        var reskin = WinReResources.ThemeText("reskin.xml");
        var ui = Text(cpio.Get($"{twres}/ui.xml")!);
        var portrait = Text(cpio.Get($"{twres}/portrait.xml")!);
        if (cpio.Get($"{twres}/splash.xml") is null)
        {
            throw new InvalidOperationException("No twres/splash.xml in the ramdisk.");
        }

        var winre = WinReResources.ThemeText("winre.xml");
        var (uiNew, uiOps) = WinReTheme.PatchUiXml(ui, vars, icons, reskin);
        var (portraitNew, portraitOps) = WinReTheme.PatchPortraitXml(portrait, winre, reskin);
        var splashNew = WinReTheme.PatchSplashXml(WinReResources.ThemeText("splash.xml"), vars);
        report?.Add($"theme: ui.xml {uiOps} reskin op(s), portrait.xml {portraitOps} op(s), winre.xml {winre.Length} B");

        // Every stock bitmap is replaced by original art under the same name.
        var stockArt = WinReResources.Folder("stock");
        var unreplaced = cpio.Entries
            .Where(e => e.Name.StartsWith($"{twres}/images/", StringComparison.Ordinal) && e.Name.EndsWith(".png", StringComparison.Ordinal))
            .Select(e => e.Name[(twres.Length + 8)..])
            .Where(n => !stockArt.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (unreplaced.Count > 0)
        {
            throw new InvalidOperationException($"Stock images without an original replacement: {string.Join(", ", unreplaced)}.");
        }
        foreach (var (name, png) in stockArt.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            cpio.PutFile($"{twres}/images/{name}", png);
        }
        report?.Add($"images: {stockArt.Count} stock image(s) replaced with original art");

        Utf8Put(cpio, $"{twres}/ui.xml", uiNew);
        Utf8Put(cpio, $"{twres}/portrait.xml", portraitNew);
        Utf8Put(cpio, $"{twres}/winre.xml", winre);
        Utf8Put(cpio, $"{twres}/splash.xml", splashNew);

        var images = WinReResources.Folder("images");
        foreach (var (name, png) in images.Where(kv => !kv.Key.StartsWith("winrecog", StringComparison.Ordinal))
                     .OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            cpio.PutFile($"{twres}/images/{name}", png);
        }
        for (var i = 0; i < gearFrames.Count; i++)
        {
            cpio.PutFile($"{twres}/images/winrecog{(i + 1).ToString("000", CultureInfo.InvariantCulture)}.png", gearFrames[i]);
        }

        // Fonts: Segoe UI from the user's own Windows, or a folder of open fonts already named for
        // the recovery (winre-*.ttf, e.g. tools/twrp-winre/fonts) for a redistributable build.
        var (fonts, openFonts) = ResolveFonts(fontsDirectory);
        foreach (var (dest, src) in fonts)
        {
            cpio.PutFile($"{twres}/fonts/{dest}", File.ReadAllBytes(src));
        }
        if (openFonts)
        {
            // OFL: every copy of the fonts carries their copyright notice and license.
            foreach (var license in Directory.EnumerateFiles(fontsDirectory, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
            {
                cpio.PutFile($"{twres}/fonts/winre-{Path.GetFileName(license)}", File.ReadAllBytes(license));
            }
        }
        report?.Add(openFonts ? $"fonts: open fonts from {fontsDirectory}" : "fonts: Segoe UI from this PC's Windows");

        // /sbin scripts (LF line endings, mode 0755).
        foreach (var (name, data) in WinReResources.Folder("sbin"))
        {
            cpio.PutFile($"sbin/{name}", NormalizeScript(data), 0x1FF); // 0755
        }

        // GPL kernel modules (ours), baked in.
        var modules = LoadModules(modulesDirectory);
        foreach (var (name, data) in modules)
        {
            cpio.PutFile($"{ModulePath}/{name}", data);
        }
        report?.Add($"modules: {modules.Count} kernel module(s) baked into /{ModulePath}");

        PatchUsbRc(cpio);
        InjectNtfsWatchdog(cpio);
        report?.Add("watchdog: ntfs-3g FUSE deadlock breaker installed as an init service");

        var gearsId = GearsId(gearsGif);
        var marker = $"builder={BuilderVersion}\nbase_sha256={baseSha}\ngears={gearsId}\n" + (openFonts ? "fonts=open\n" : "");
        Utf8Put(cpio, $"{twres}/winre-build.txt", marker);

        // Repack.
        var plainNew = cpio.Serialize();
        boot.Ramdisk = Lzma.LzmaAlone.Compress(plainNew);
        if (!Lzma.LzmaAlone.Decompress(boot.Ramdisk).AsSpan().SequenceEqual(plainNew))
        {
            throw new InvalidOperationException("Recompressed ramdisk does not decompress back; aborting.");
        }

        var img = boot.Serialize(refreshId: true, keepTail: false);
        if (img.Length > AndroidBootImage.RecoveryPartitionBytes)
        {
            throw new InvalidOperationException(
                $"Built image does not fit RECOVERY: {img.Length:N0} > {AndroidBootImage.RecoveryPartitionBytes:N0} B.");
        }

        // Post-build assertions: only the ramdisk and the four patch bytes changed.
        var rebuilt = AndroidBootImage.Parse(img);
        if (!rebuilt.Dt.AsSpan().SequenceEqual(baseDt) || !rebuilt.Second.AsSpan().SequenceEqual(baseSecond))
        {
            throw new InvalidOperationException("Device-tree or second stage changed unexpectedly.");
        }
        if (!rebuilt.Kernel.AsSpan().SequenceEqual(PowerOffRoutePatch.Patch(baseKernel)))
        {
            throw new InvalidOperationException("Kernel is not exactly the base kernel plus the power-off patch.");
        }

        report?.Add($"image: {img.Length:N0} B, sha256 {Sha256(img)[..16]}...");
        return new WinReBuildResult(img, Sha256(img), BuilderVersion, baseSha, gearsId);
    }

    private IReadOnlyList<(string Name, byte[] Data)> LoadModules(string? overrideDir)
    {
        var names = new[]
        {
            "rwd1_ack.ko", "rwd1_evidence_reader.ko",
            // The supervised clearers for an invalid RWD1 word and the P3/SMP1 startup
            // record: baking them in lets the recovery (and the Troubleshoot actions)
            // fix the Samsung-logo startup gate on the phone, with no host attached.
            "rwd1_clear_poc.ko", "pram_smp_clear_poc.ko",
        };
        var list = new List<(string, byte[])>();
        foreach (var name in names)
        {
            if (overrideDir is not null && File.Exists(Path.Combine(overrideDir, name)))
            {
                list.Add((name, File.ReadAllBytes(Path.Combine(overrideDir, name))));
            }
            else
            {
                list.Add((name, WinReResources.Bytes($"modules.{name}")));
            }
        }
        return list;
    }

    private const string UsbAnchor = "    setprop sys.usb.controller 10c00000.dwc3\n";

    /// <summary>Init service that runs the ntfs-3g FUSE deadlock breaker.</summary>
    private const string WatchdogService = "winre_ntfswd";
    private const string WatchdogScript = "/sbin/winre-ntfs-watchdog.sh";

    /// <summary>
    /// Registers <see cref="WatchdogScript"/> as an init service started at boot. It has to run
    /// from init, not from postrecoveryboot.sh, because the ntfs-3g self-deadlock strikes while
    /// the recovery binary is still mounting /data - long before any TWRP boot script runs, and
    /// with adb/MTP still down. Started here it is already watching when /data is mounted.
    /// </summary>
    private static void InjectNtfsWatchdog(CpioArchive cpio)
    {
        var entry = cpio.Get("init.recovery.service.rc");
        if (entry is null)
        {
            return;
        }
        var text = Encoding.UTF8.GetString(entry.Data).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Contains(WatchdogService, StringComparison.Ordinal))
        {
            return; // already injected (idempotent rebuild)
        }
        var block = "\n"
            + $"service {WatchdogService} {WatchdogScript}\n"
            + "    oneshot\n"
            + "    seclabel u:r:recovery:s0\n"
            + "\n"
            + "on boot\n"
            + $"    start {WatchdogService}\n";
        entry.Data = Encoding.UTF8.GetBytes(text.TrimEnd('\n') + "\n" + block);
    }

    private static void PatchUsbRc(CpioArchive cpio)
    {
        var entry = cpio.Get("init.recovery.usb.rc");
        if (entry is null)
        {
            return;
        }
        var text = Encoding.UTF8.GetString(entry.Data);
        if (text.Contains("setprop sys.usb.config adb", StringComparison.Ordinal) || Count(text, UsbAnchor) != 1)
        {
            return;
        }
        var patch = UsbAnchor
            + "    # adb must not depend on the recovery binary surviving: nothing else\n"
            + "    # sets sys.usb.config, and TWRP skips its USB setup after a crash.\n"
            + "    setprop sys.usb.config adb\n";
        entry.Data = Encoding.UTF8.GetBytes(text.Replace(UsbAnchor, patch, StringComparison.Ordinal));
    }

    private static int Count(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += sub.Length;
        }
        return n;
    }

    private static byte[] NormalizeScript(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text);
    }

    private static string Text(CpioEntry e) => Encoding.UTF8.GetString(e.Data);

    private static void Utf8Put(CpioArchive cpio, string name, string text) =>
        cpio.PutFile(name, Encoding.UTF8.GetBytes(text));

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
}
