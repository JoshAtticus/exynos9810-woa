# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Route USB-connected TWRP "Power Off" through Samsung's normal reboot hook.

Stock TWRP on this device, when connected over USB, does not actually power the
phone off from the GUI "Turn off" action - it drops into a state that looks hung.
A four-byte change to one branch in the recovery kernel sends that path through
Samsung's `sec_reboot` hook instead, which powers down cleanly.

This is a byte patch on the KERNEL, which the WinRE build carries across from the
official TWRP image untouched, so it is gated on the kernel's own SHA-256 plus
the exact bytes at the patch site: it refuses to touch anything that is not a
known-good star2lte (S9+) or starlte (S9) TWRP 3.7.0_9-0 kernel. Both kernels
gate through the same mechanism; the starlte offsets were derived by windowed
disassembly of sec_reboot/sec_power_off in the same layout the star2lte ones
were measured in. The boot image's SHA-1 id is recomputed by the caller after
patching (BootImage.serialize(refresh_id=True)).

The star2lte offsets and signatures are the ones measured on the deployed device
(the research `patch_twrp_poweroff_route.py`); here they gate the *kernel* rather
than a whole 68 MiB partition image, because the WinRE build starts from the
42 MiB official file, not a partition dump.
"""

from __future__ import annotations

import hashlib
import struct

from dataclasses import dataclass
from typing import Optional

# Kernel images of the official TWRP 3.7.0_9-0 recoveries; both profiles gate on
# their own sha256 plus the bytes at the patch site.
SEC_REBOOT_SIG = bytes.fromhex(
    "fd7bbca9e203002afd030091f35301a9"
    "f55b02a9f30301aadf4203d5e11c00b4"
)
# star2lte sec_power_off prologue.
SEC_POWER_OFF_SIG_STAR2LTE = bytes.fromhex(
    "fd7bb9a900500090010080d2fd030091"
    "00403e91f55b02a9f35301a9f76303a9"
    "f96b04a90097ff97"
)
# starlte sec_power_off prologue (frame save differs from star2lte).
SEC_POWER_OFF_SIG_STARLTE = bytes.fromhex(
    "fd7bb9a920500090010080d2fd030091"
    "00800591f55b02a9f35301a9f76303a9"
)

RESTART_CALL_BEFORE = 0xD63F0040  # blr x2
RESTART_CALL_AFTER = 0x97FFFE00   # bl sec_reboot


@dataclass
class Profile:
    kernel_sha256: str
    sec_reboot_offset: int
    sec_power_off_offset: int
    restart_call_offset: int
    sec_power_off_sig: bytes


STAR2LTE = Profile(
    "1228ba84942d1b16f565c42af242eaf1ce084eb373f71cfa40fee74a7e7ef594",
    0x00A912CC, 0x00A916C4, 0x00A91ACC, SEC_POWER_OFF_SIG_STAR2LTE)

# Derived from the twrp-3.7.0_9-0-starlte kernel the same way as the star2lte
# one: sec_reboot at 0xA89140, sec_power_off at 0xA89538 (same 0x3F8 spacing),
# restart call (blr x2) at 0xA89940. The functions sit 0x800 apart in both
# kernels, so the encoded BL is identical (0x97FFFE00).
STARLTE = Profile(
    "68fa43f08ebc6ccc02701ef5213f9a7983bdb1d90617704ecec575e3376a1610",
    0x00A89140, 0x00A89538, 0x00A89940, SEC_POWER_OFF_SIG_STARLTE)

PROFILES = (STARLTE, STAR2LTE)


class PatchError(RuntimeError):
    pass


def _encode_bl(source: int, target: int) -> int:
    delta = target - source
    if delta % 4 != 0:
        raise PatchError("BL target is not instruction-aligned")
    imm = delta // 4
    if not -(1 << 25) <= imm < (1 << 25):
        raise PatchError("BL target is out of range")
    return 0x94000000 | (imm & 0x03FFFFFF)


def _match(kernel: bytes) -> Optional[Profile]:
    """The profile whose known kernel this is, or None."""
    digest = hashlib.sha256(kernel).hexdigest()
    return next((p for p in PROFILES if p.kernel_sha256 == digest), None)


def is_patchable(kernel: bytes) -> bool:
    """True if this is a known kernel and the patch site is unmodified."""
    p = _match(kernel)
    if p is None:
        return False
    return (kernel[p.sec_reboot_offset:p.sec_reboot_offset + len(SEC_REBOOT_SIG)] == SEC_REBOOT_SIG
            and kernel[p.sec_power_off_offset:p.sec_power_off_offset + len(p.sec_power_off_sig)]
            == p.sec_power_off_sig
            and struct.unpack_from("<I", kernel, p.restart_call_offset)[0] == RESTART_CALL_BEFORE)


def patch_kernel(kernel: bytes) -> bytes:
    """Apply the power-off route patch, refusing anything but a known kernel."""
    p = _match(kernel)
    if p is None:
        digest = hashlib.sha256(kernel).hexdigest()
        raise PatchError(
            f"kernel sha256 {digest[:16]}... is not a known TWRP 3.7.0_9-0 kernel "
            "(starlte/star2lte); refusing to patch")
    if kernel[p.sec_reboot_offset:p.sec_reboot_offset + len(SEC_REBOOT_SIG)] != SEC_REBOOT_SIG:
        raise PatchError("sec_reboot signature mismatch")
    if kernel[p.sec_power_off_offset:p.sec_power_off_offset + len(p.sec_power_off_sig)] \
            != p.sec_power_off_sig:
        raise PatchError("sec_power_off signature mismatch")
    actual = struct.unpack_from("<I", kernel, p.restart_call_offset)[0]
    if actual != RESTART_CALL_BEFORE:
        raise PatchError(f"restart call precondition failed: 0x{actual:08X}")

    encoded = _encode_bl(p.restart_call_offset, p.sec_reboot_offset)
    if encoded != RESTART_CALL_AFTER:
        raise PatchError(f"derived BL mismatch: 0x{encoded:08X}")

    out = bytearray(kernel)
    struct.pack_into("<I", out, p.restart_call_offset, encoded)
    changed = [i for i in range(len(kernel)) if kernel[i] != out[i]]
    if changed != list(range(p.restart_call_offset, p.restart_call_offset + 4)):
        raise PatchError("patch changed unexpected bytes")
    return bytes(out)
