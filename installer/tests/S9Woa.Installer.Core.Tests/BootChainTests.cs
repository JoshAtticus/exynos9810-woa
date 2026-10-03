// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Image;

namespace S9Woa.Installer.Core.Tests;

public class BootChainTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("s9woa-boot").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteCatalog(string dir, params (string File, string Windows, string[] Media, string Loader, string Kernel, string? Device)[] images)
    {
        Directory.CreateDirectory(dir);
        var entries = new List<object>();
        foreach (var (file, windows, media, loader, kernel, device) in images)
        {
            var bytes = new byte[4096];
            new Random(file.Length).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(dir, file), bytes);
            var entry = new Dictionary<string, object>
            {
                ["file"] = file,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                ["windows"] = windows,
                ["mediaBuilds"] = media,
                ["loaderSha256"] = loader,
                ["kernelSha256"] = kernel,
            };
            if (device is not null)
            {
                entry["device"] = device;
            }
            entries.Add(entry);
        }
        File.WriteAllText(Path.Combine(dir, FirmwareCatalog.FileName),
            JsonSerializer.Serialize(new { schema = FirmwareCatalog.Schema, images = entries }));
        return dir;
    }

    [Fact]
    public void FirmwareIsChosenByTheExactLoaderAndKernel()
    {
        var dir = WriteCatalog(Path.Combine(_root, "uefi"),
            ("a.img", "22621.2428", ["22631.2428"], "aa", "ka", null),
            ("b.img", "22621.7582", ["22631.7584"], "bb", "kb", null));
        var catalog = FirmwareCatalog.Load(dir)!;

        Assert.Equal("a.img", catalog.ForBootFiles("AA", "KA")!.File);
        Assert.Null(catalog.ForBootFiles("aa", "kb"));
        Assert.Equal("b.img", catalog.ForMediaBuild("22631.7584")!.File);
        Assert.Equal("a.img", catalog.ForMediaBuild("22621.2428")!.File);
        Assert.Null(catalog.ForMediaBuild("22631.7633"));
        Assert.Equal("22621.2428, 22631.2428, 22621.7582, 22631.7584", catalog.SupportedBuilds);
        Assert.All(catalog.Images, i => Assert.True(catalog.Verify(i)));

        var copy = catalog.CopyTo(Path.Combine(_root, "copy"));
        Assert.Equal(2, copy.Present(1 << 20).Count);
    }

    [Fact]
    public void AnyWindowsBuildGetsAFirmwareExactOrNearest()
    {
        var dir = WriteCatalog(Path.Combine(_root, "any"),
            ("a.img", "22621.2428", ["22631.2428"], "aa", "ka", null),
            ("b.img", "22621.7582", ["22631.7584"], "bb", "kb", null));
        var catalog = FirmwareCatalog.Load(dir)!;

        // Exact: the image built for this loader/kernel, or a listed media build before the image exists.
        Assert.Equal(new FirmwareChoice(catalog.Images[0], true), catalog.Choose("22631.7633", "AA", "ka"));
        Assert.Equal(new FirmwareChoice(catalog.Images[1], true), catalog.Choose("22631.7584"));

        // Not built for: same kernel family first, then the nearest revision.
        Assert.Equal(new FirmwareChoice(catalog.Images[1], false), catalog.Choose("22631.7633"));
        Assert.Equal(new FirmwareChoice(catalog.Images[0], false), catalog.Choose("22621.2861"));
        // A listed media build whose built loader/kernel differ is not exact.
        Assert.Equal(new FirmwareChoice(catalog.Images[0], false), catalog.Choose("22621.2428", "cc", "kc"));
        // Another family, or an unknown build: the newest image.
        Assert.Equal(new FirmwareChoice(catalog.Images[1], false), catalog.Choose("26100.4061"));
        Assert.Equal(new FirmwareChoice(catalog.Images[1], false), catalog.Choose(null));
    }

    [Fact]
    public void FirmwareIsChosenPerDevice()
    {
        var dir = WriteCatalog(Path.Combine(_root, "dev"),
            ("s9.img", "22621.2428", [], "aa", "ka", "starlte"),
            ("s9plus.img", "22621.2428", [], "aa", "ka", "star2lte"));
        var catalog = FirmwareCatalog.Load(dir)!;
        Assert.Equal("starlte", catalog.Images[0].Device);
        Assert.Equal("star2lte", catalog.Images[1].Device);

        Assert.Equal("s9.img", catalog.Choose("22621.2428", "aa", "ka", "starlte")!.Image.File);
        Assert.Equal("s9plus.img", catalog.Choose("22621.2428", "aa", "ka", "star2lte")!.Image.File);
        Assert.True(catalog.Choose("22621.2428", "aa", "ka", "starlte")!.Exact);
        // No image for an unknown model.
        Assert.Null(catalog.Choose("22621.2428", "aa", "ka", "starqlte"));
        // No device filter: legacy behaviour, first match wins.
        Assert.Equal("s9.img", catalog.Choose("22621.2428", "aa", "ka")!.Image.File);
    }

    [Fact]
    public void LegacyCatalogEntriesDefaultToStar2lte()
    {
        var dir = WriteCatalog(Path.Combine(_root, "legacy"),
            ("a.img", "22621.2428", [], "aa", "ka", null));
        var catalog = FirmwareCatalog.Load(dir)!;
        Assert.Equal("star2lte", catalog.Images[0].Device);
        Assert.Equal("a.img", catalog.Choose("22621.2428", "aa", "ka", "star2lte")!.Image.File);
        Assert.Null(catalog.Choose("22621.2428", "aa", "ka", "starlte"));
    }

    [Fact]
    public void CatalogRefusesTamperedImagesAndEscapes()
    {
        var dir = WriteCatalog(Path.Combine(_root, "t"), ("a.img", "22621.2428", [], "aa", "ka", null));
        File.WriteAllBytes(Path.Combine(dir, "a.img"), new byte[4096]);
        var catalog = FirmwareCatalog.Load(dir)!;
        Assert.False(catalog.Verify(catalog.Images[0]));
        Assert.Throws<InvalidDataException>(() => catalog.CopyTo(Path.Combine(_root, "out")));

        File.WriteAllText(Path.Combine(dir, FirmwareCatalog.FileName), JsonSerializer.Serialize(new
        {
            schema = FirmwareCatalog.Schema,
            images = new[] { new { file = @"..\evil.img", sha256 = "", windows = "", loaderSha256 = "", kernelSha256 = "" } },
        }));
        Assert.Throws<InvalidDataException>(() => FirmwareCatalog.Load(dir));
    }

    [Theory]
    [InlineData("Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.28089\r\n\r\nDetails for image\r\nIndex : 2\r\nName : Windows 11 IoT Enterprise\r\nArchitecture : arm64\r\nVersion : 10.0.22621\r\nServicePack Build : 2428\r\n", "22621.2428")]
    [InlineData("Version: 10.0.26100.1\r\nVersion : 10.0.22631\r\nServicePack Build : 7633\r\n", "22631.7633")]
    [InlineData("Version: 10.0.26100.1\r\n", null)]
    public void ReadsTheMediaBuild(string dism, string? expected) => Assert.Equal(expected, WindowsMedia.ParseBuild(dism));

    private static byte[] Record(uint state, uint owner, uint phase, uint reason, bool corrupt = false)
    {
        var w = new uint[16];
        w[0] = RecoveryRecord.Magic;
        w[1] = (64u << 16) | 1;
        w[2] = 0x044eec33;
        w[3] = ~w[2];
        w[4] = state;
        w[5] = owner;
        w[6] = phase;
        w[7] = reason;
        w[8] = 0x20000000;
        w[9] = 0x20;
        w[11] = 0xFFFFFFFF;
        var ck = 0xA5A55A5Au;
        for (var i = 0; i < 12; i++)
        {
            ck ^= w[i];
        }
        w[12] = corrupt ? ck ^ 1 : ck;
        w[13] = ~w[12];
        var b = new byte[64];
        for (var i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), w[i]);
        }
        return b;
    }

    [Fact]
    public void DecodesTheWatchdogRecordCapturedOnThePhone()
    {
        // The record read on the phone after the first failed start (2026-09-29).
        var r = RecoveryRecord.Parse(Record(0xA0, 2, 0x0E, 2))!;
        Assert.True(r.ChecksumOk);
        Assert.True(r.IsRecoveryPending);
        Assert.Equal("recovery pending (phase: recovery route; reason: stale boot owner)", r.ToString());

        Assert.False(RecoveryRecord.Parse(Record(0xA0, 2, 0x0E, 2, corrupt: true))!.ChecksumOk);
        Assert.Null(RecoveryRecord.Parse(Enumerable.Repeat((byte)0xFF, 64).ToArray()));
        Assert.Null(RecoveryRecord.Parse(new byte[64]));
    }

    [Theory]
    [InlineData("rwd1_ack status=0 empty=0 valid=1 cleared=1 state_before=0x000000a0 reason=0x00000002 generation=0x044eec33\n", 0, true, 0xA0u)]
    [InlineData("rwd1_ack status=-1 empty=0 valid=1 cleared=0 state_before=0x00000090 reason=0x00000009", -1, false, 0x90u)]
    [InlineData("rwd1_ack status=-117 empty=1 valid=0 cleared=0 state=00000000", -117, false, 0u)]
    public void ReadsTheAckModuleResult(string text, int status, bool cleared, uint before)
    {
        var ack = RecoveryAck.Parse(text)!;
        Assert.Equal(status, ack.Status);
        Assert.Equal(cleared, ack.Cleared);
        Assert.Equal(before, ack.StateBefore);
    }

    [Fact]
    public void JudgesTheStartupRecordsOnEveryByte()
    {
        // This morning's records after a power loss: a few decayed bits, first words non-zero.
        var rwd1 = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(rwd1, 0x00100000);
        BinaryPrimitives.WriteUInt32LittleEndian(rwd1.AsSpan(8), 0x00400000);
        var p3 = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(p3, 0x00000010);
        BinaryPrimitives.WriteUInt32LittleEndian(p3.AsSpan(20), 0x00000010);
        var decayed = new StartupRecords(rwd1, p3);
        Assert.False(decayed.Rwd1Clear);
        Assert.False(decayed.P3Clear);
        Assert.False(decayed.ReadyToStart);
        Assert.StartsWith("unreadable (first word 0x00100000, 2 of 64 bytes set)", decayed.Rwd1Kind, StringComparison.Ordinal);
        Assert.StartsWith("invalid (first word 0x00000010, 2 of 128 bytes set)", decayed.P3Kind, StringComparison.Ordinal);

        // The record that hung the reference phone: a ZERO first word with stray bits further in.
        // A magic-only check calls this clean; the firmware's gate halts on it.
        var hidden = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(hidden.AsSpan(4), 0x40000000);
        BinaryPrimitives.WriteUInt32LittleEndian(hidden.AsSpan(8), 0x00000800);
        Assert.False(new StartupRecords(new byte[64], hidden).P3Clear);

        // All-ones (a cold erase) is not all-zero: the gate only passes it under extra guards.
        Assert.False(new StartupRecords(new byte[64], Enumerable.Repeat((byte)0xFF, 128).ToArray()).P3Clear);
        Assert.Equal("erased (all ones)", new StartupRecords(new byte[64], Enumerable.Repeat((byte)0xFF, 128).ToArray()).P3Kind);

        // A left-over, well-formed RWD1 record from an earlier start still has to go.
        var stale = new StartupRecords(Record(0x20, 2, 2, 0), new byte[128]);
        Assert.False(stale.Rwd1Clear);
        Assert.StartsWith("left over from an earlier start: SEC", stale.Rwd1Kind, StringComparison.Ordinal);

        Assert.True(new StartupRecords(new byte[64], new byte[128]).ReadyToStart);
    }

    [Fact]
    public void RecognisesAWellFormedP3Record()
    {
        var p3 = new uint[32];
        p3[0] = StartupRecords.P3Magic;
        p3[1] = StartupRecords.P3VersionLength;
        p3[2] = 0xA55E3013;
        p3[5] = 0x60;
        var xor = StartupRecords.P3Magic;
        for (var i = 1; i < 31; i++)
        {
            xor ^= p3[i];
        }
        p3[31] = xor; // makes the XOR over all 32 words zero
        var bytes = new byte[128];
        for (var i = 0; i < 32; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), p3[i]);
        }
        Assert.True(StartupRecords.IsValidP3(bytes));
        Assert.Equal("left over from an earlier start", new StartupRecords(new byte[64], bytes).P3Kind);
        bytes[40] ^= 1;
        Assert.False(StartupRecords.IsValidP3(bytes));
    }
}
