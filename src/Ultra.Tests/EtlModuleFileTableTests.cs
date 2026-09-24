// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtlModuleFileTable"/> and <see cref="EtlModuleFile"/>: a module file is a unique
/// (lower-cased path, image base) pair, like <c>TraceModuleFiles</c>.
/// </summary>
[TestClass]
public class EtlModuleFileTableTests
{
    [TestMethod]
    public void SamePathAndBaseReturnsSameIndexRegardlessOfCase()
    {
        var table = new EtlModuleFileTable();
        var a = table.GetOrCreate(@"C:\Windows\System32\NTDLL.dll", 0x7FF800000000);
        var b = table.GetOrCreate(@"c:\windows\system32\ntdll.DLL", 0x7FF800000000);

        Assert.AreEqual(a, b);
        Assert.AreEqual(1, table.Count);
    }

    [TestMethod]
    public void PathIsLowerCased()
    {
        var table = new EtlModuleFileTable();
        var index = table.GetOrCreate(@"C:\Games\KSP_x64.exe", 0x140000000);

        Assert.AreEqual(@"c:\games\ksp_x64.exe", table[index].FilePath);
        Assert.IsTrue(table[index].HasFilePath);
    }

    [TestMethod]
    public void DifferentImageBaseOfSamePathReturnsDifferentIndex()
    {
        var table = new EtlModuleFileTable();
        var a = table.GetOrCreate(@"C:\x\foo.dll", 0x10000000);
        var b = table.GetOrCreate(@"C:\x\foo.dll", 0x20000000);

        Assert.AreNotEqual(a, b);
        Assert.AreEqual(2, table.Count);
    }

    [TestMethod]
    public void IndicesAreConsecutiveAndMatchFileIndex()
    {
        var table = new EtlModuleFileTable();
        var a = table.GetOrCreate(@"C:\x\a.dll", 1);
        var b = table.GetOrCreate(@"C:\x\b.dll", 1);

        Assert.AreEqual(0, a);
        Assert.AreEqual(1, b);
        Assert.AreEqual(a, table[a].Index);
        Assert.AreEqual(b, table[b].Index);
    }

    [TestMethod]
    public void PathlessFileIsPlaceholderManagedModule()
    {
        var table = new EtlModuleFileTable();
        var index = table.GetOrCreate(null, 0);
        var file = table[index];

        Assert.IsFalse(file.HasFilePath);
        Assert.AreEqual(EtlModuleFile.UnknownManagedModulePath, file.FilePath);
        Assert.AreEqual("ManagedModule", file.FilePath);
    }

    [TestMethod]
    public void PathlessFilesAreNeverShared()
    {
        var table = new EtlModuleFileTable();
        var a = table.GetOrCreate(null, 0x1000);
        var b = table.GetOrCreate(null, 0x1000);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void SetFileNameNamesPlaceholderAndMakesItFindable()
    {
        var table = new EtlModuleFileTable();
        var index = table.GetOrCreate(null, 0x5000);
        var file = table[index];
        Assert.AreEqual("ManagedModule", file.Name);

        table.SetFileName(file, @"C:\Game\Managed\Assembly-CSharp.DLL");

        Assert.IsTrue(file.HasFilePath);
        Assert.AreEqual(@"c:\game\managed\assembly-csharp.dll", file.FilePath);
        Assert.AreEqual("assembly-csharp", file.Name);
        Assert.AreEqual(index, table.GetOrCreate(@"C:\Game\Managed\Assembly-CSharp.dll", 0x5000));
        Assert.AreEqual(1, table.Count);
    }

    [TestMethod]
    public void SetFileNameDoesNotStealExistingEntry()
    {
        var table = new EtlModuleFileTable();
        var existing = table.GetOrCreate(@"C:\x\a.dll", 0x5000);
        var placeholder = table.GetOrCreate(null, 0x5000);

        table.SetFileName(table[placeholder], @"C:\x\a.dll");

        Assert.AreEqual(existing, table.GetOrCreate(@"C:\x\a.dll", 0x5000));
    }

    [TestMethod]
    public void NameDerivesFromLowerCasedPath()
    {
        var table = new EtlModuleFileTable();
        var file = table[table.GetOrCreate(@"C:\Windows\System32\KernelBase.dll", 0x1000)];

        Assert.AreEqual("kernelbase", file.Name);
    }

    [TestMethod]
    public void NameKeepsNonDllExeExtension()
    {
        var table = new EtlModuleFileTable();
        var file = table[table.GetOrCreate(@"\SystemRoot\System32\drivers\NVLDDMKM.SYS", 0xFFFFF80000000000)];

        Assert.AreEqual("nvlddmkm.sys", file.Name);
    }

    [TestMethod]
    public void ImageEndIsBasePlusSize()
    {
        var table = new EtlModuleFileTable();
        var file = table[table.GetOrCreate(@"C:\x\a.dll", 0x7FF800000000)];
        file.ImageSize = 0x12000;

        Assert.AreEqual(0x7FF800012000UL, file.ImageEnd);
    }
}
