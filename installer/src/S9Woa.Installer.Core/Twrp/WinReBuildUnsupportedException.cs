// SPDX-License-Identifier: BSD-2-Clause-Patent
using System;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// The base image is known-good but this builder cannot reskin it (e.g. the
/// starlte kernel's power-off patch has no derived offsets). The caller may fall
/// back to flashing the base image as-is; distinct from a corrupt or wrong image,
/// which stays an <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class WinReBuildUnsupportedException : InvalidOperationException
{
    public WinReBuildUnsupportedException(string message) : base(message)
    {
    }
}
