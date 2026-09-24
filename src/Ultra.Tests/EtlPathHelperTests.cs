// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars"/>, which names processes and modules
/// the way <c>TraceLog</c> does: only a <c>.dll</c>/<c>.exe</c> extension is stripped.
/// </summary>
[TestClass]
public class EtlPathHelperTests
{
    [TestMethod]
    [DataRow(@"C:\Windows\System32\ntdll.dll", "ntdll")]
    [DataRow(@"C:\Games\KSP_x64.exe", "KSP_x64")]
    [DataRow(@"C:\Windows\System32\KERNEL32.DLL", "KERNEL32")]
    [DataRow(@"C:\Games\Game.ExE", "Game")]
    public void StripsDllAndExeExtensionsCaseInsensitively(string path, string expected)
    {
        Assert.AreEqual(expected, EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(path));
    }

    [TestMethod]
    [DataRow(@"\SystemRoot\System32\drivers\nvlddmkm.sys", "nvlddmkm.sys")]
    [DataRow(@"C:\Program Files\app\mono-2.0-bdwgc.pdb", "mono-2.0-bdwgc.pdb")]
    [DataRow(@"C:\data\file.dll.config", "file.dll.config")]
    public void KeepsOtherExtensions(string path, string expected)
    {
        Assert.AreEqual(expected, EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(path));
    }

    [TestMethod]
    public void HandlesForwardSlashSeparators()
    {
        Assert.AreEqual("libfoo", EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars("/usr/lib/libfoo.dll"));
    }

    [TestMethod]
    public void HandlesMixedSeparators()
    {
        Assert.AreEqual("bar", EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(@"C:\one/two\bar.exe"));
    }

    [TestMethod]
    public void HandlesPathWithoutDirectory()
    {
        Assert.AreEqual("clr", EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars("clr.dll"));
    }

    [TestMethod]
    public void HandlesFileWithoutExtension()
    {
        Assert.AreEqual("System", EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(@"C:\Windows\System"));
    }

    [TestMethod]
    public void DotInDirectoryIsNotTreatedAsExtension()
    {
        Assert.AreEqual("file", EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(@"C:\my.dll\file"));
    }

    [TestMethod]
    public void KeepsTrailingDot()
    {
        Assert.AreEqual("name.", EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(@"C:\dir\name."));
    }

    [TestMethod]
    public void EmptyPathYieldsEmptyName()
    {
        Assert.AreEqual(string.Empty, EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(string.Empty));
    }

    [TestMethod]
    public void TrailingSeparatorYieldsEmptyName()
    {
        Assert.AreEqual(string.Empty, EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(@"C:\dir\"));
    }
}
