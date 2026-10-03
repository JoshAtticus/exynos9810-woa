// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// Routes USB-connected TWRP "Power Off" through Samsung's <c>sec_reboot</c> hook
/// with a four-byte change to one branch in the recovery kernel, so the GUI
/// "Turn off" action powers the phone down cleanly instead of hanging. The patch
/// is gated on the kernel's own SHA-256 plus the exact bytes at the patch site,
/// so it refuses anything but the known-good star2lte/starlte TWRP 3.7.0_9-0
/// kernels. The boot image id is recomputed by the caller
/// (<see cref="AndroidBootImage.Serialize"/>). Mirrors the reference Python
/// <c>poweroff.py</c>.
/// </summary>
public static class PowerOffRoutePatch
{
    public sealed record Profile(
        string KernelSha256,
        int SecRebootOffset,
        int SecPowerOffOffset,
        int RestartCallOffset,
        string SecRebootSigHex,
        string SecPowerOffSigHex);

    /// <summary>Common sec_reboot prologue; identical in both TWRP kernels.</summary>
    private const string SecRebootSig =
        "fd7bbca9e203002afd030091f35301a9f55b02a9f30301aadf4203d5e11c00b4";

    // star2lte < 0x00A916C4 >, starlte < 0x00A89538 >; differ from frame save on.
    private const string SecPowerOffSigStar2Lte =
        "fd7bb9a900500090010080d2fd03009100403e91f55b02a9f35301a9f76303a9f96b04a90097ff97";

    private const string SecPowerOffSigStarLte =
        "fd7bb9a920500090010080d2fd03009100800591f55b02a9f35301a9f76303a9";

    private const uint RestartCallBefore = 0xD63F0040; // blr x2
    private const uint RestartCallAfter = 0x97FFFE00;  // bl sec_reboot

    public static readonly Profile Star2Lte = new(
        "1228ba84942d1b16f565c42af242eaf1ce084eb373f71cfa40fee74a7e7ef594",
        0x00A912CC, 0x00A916C4, 0x00A91ACC, SecRebootSig, SecPowerOffSigStar2Lte);

    /// <summary>
    /// Derived the same way as the star2lte one (disassembly of the
    /// twrp-3.7.0_9-0-starlte kernel): sec_reboot at 0xA89140, sec_power_off at
    /// 0xA89538 (same 0x3F8 spacing as star2lte), restart call at 0xA89940. The
    /// two functions again sit 0x800 bytes apart, so the encoded BL is identical.
    /// </summary>
    public static readonly Profile StarLte = new(
        "68fa43f08ebc6ccc02701ef5213f9a7983bdb1d90617704ecec575e3376a1610",
        0x00A89140, 0x00A89538, 0x00A89940, SecRebootSig, SecPowerOffSigStarLte);

    private static readonly Profile[] Profiles = [StarLte, Star2Lte];

    private static bool Matches(byte[] kernel, out Profile profile)
    {
        profile = null!;
        var digest = Convert.ToHexString(SHA256.HashData(kernel)).ToLowerInvariant();
        var match = Profiles.FirstOrDefault(p => p.KernelSha256 == digest);
        if (match is null)
        {
            return false;
        }
        profile = match;
        return true;
    }

    /// <summary>True if this is a known kernel and the patch site is unmodified.</summary>
    public static bool IsPatchable(byte[] kernel)
    {
        if (!Matches(kernel, out var p))
        {
            return false;
        }
        return SiteOk(kernel, p);
    }

    private static bool SiteOk(byte[] kernel, Profile p) =>
        SigAt(kernel, p.SecRebootOffset, p.SecRebootSigHex)
        && SigAt(kernel, p.SecPowerOffOffset, p.SecPowerOffSigHex)
        && p.RestartCallOffset + 4 <= kernel.Length
        && BinaryPrimitives.ReadUInt32LittleEndian(kernel.AsSpan(p.RestartCallOffset)) == RestartCallBefore;

    /// <summary>Apply the patch, refusing anything but a known kernel.</summary>
    public static byte[] Patch(byte[] kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (!Matches(kernel, out var p))
        {
            var digest = Convert.ToHexString(SHA256.HashData(kernel)).ToLowerInvariant();
            throw new InvalidOperationException($"Kernel SHA-256 {digest[..16]}... is not a known TWRP 3.7.0_9-0 kernel; refusing to patch.");
        }
        if (!SigAt(kernel, p.SecRebootOffset, p.SecRebootSigHex))
        {
            throw new InvalidOperationException("sec_reboot signature mismatch.");
        }
        if (!SigAt(kernel, p.SecPowerOffOffset, p.SecPowerOffSigHex))
        {
            throw new InvalidOperationException("sec_power_off signature mismatch.");
        }
        var actual = BinaryPrimitives.ReadUInt32LittleEndian(kernel.AsSpan(p.RestartCallOffset));
        if (actual != RestartCallBefore)
        {
            throw new InvalidOperationException($"Restart call precondition failed: 0x{actual:X8}.");
        }
        var encoded = EncodeBl(p.RestartCallOffset, p.SecRebootOffset);
        if (encoded != RestartCallAfter)
        {
            throw new InvalidOperationException($"Derived BL mismatch: 0x{encoded:X8}.");
        }

        var patched = (byte[])kernel.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(p.RestartCallOffset), encoded);
        for (var i = 0; i < kernel.Length; i++)
        {
            var changed = kernel[i] != patched[i];
            var inSite = i >= p.RestartCallOffset && i < p.RestartCallOffset + 4;
            if (changed != inSite)
            {
                throw new InvalidOperationException("Patch changed unexpected bytes.");
            }
        }
        return patched;
    }

    private static bool SigAt(byte[] data, int offset, string sigHex) =>
        SigAt(data, offset, Convert.FromHexString(sigHex));

    private static bool SigAt(byte[] data, int offset, byte[] sig) =>
        offset + sig.Length <= data.Length && data.AsSpan(offset, sig.Length).SequenceEqual(sig);

    private static uint EncodeBl(int source, int target)
    {
        var delta = target - source;
        if (delta % 4 != 0)
        {
            throw new InvalidOperationException("BL target is not instruction-aligned.");
        }
        var imm = delta / 4;
        if (imm < -(1 << 25) || imm >= (1 << 25))
        {
            throw new InvalidOperationException("BL target is out of range.");
        }
        return 0x94000000u | ((uint)imm & 0x03FFFFFF);
    }
}
