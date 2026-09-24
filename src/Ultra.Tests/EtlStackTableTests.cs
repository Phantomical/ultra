// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtlStackTable"/>, the interned call-stack tree keyed by (parent, raw address) with an
/// open-addressing hash index.
/// </summary>
[TestClass]
public class EtlStackTableTests
{
    private const ulong UserAddress = 0x00007FF6_1234_5678;
    private const ulong KernelAddress = 0xFFFFF800_1234_5678;

    [TestMethod]
    public void RootForProcessIsNegativeAndDistinctPerProcess()
    {
        var model = new EtlTraceModel();
        var p1 = model.GetOrCreateProcess(100, 10);
        var p2 = model.GetOrCreateProcess(200, 10);

        var r1 = EtlStackTable.RootForProcess(p1);
        var r2 = EtlStackTable.RootForProcess(p2);

        Assert.IsLessThan(0, r1);
        Assert.IsLessThan(0, r2);
        Assert.AreNotEqual(r1, r2);
    }

    [TestMethod]
    public void InternSameParentAndAddressReturnsSameNode()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);
        var root = EtlStackTable.RootForProcess(process);

        var a = model.Stacks.Intern(root, UserAddress, process, model);
        var b = model.Stacks.Intern(root, UserAddress, process, model);

        Assert.AreEqual(a, b);
        Assert.AreEqual(1, model.Stacks.Count);
    }

    [TestMethod]
    public void InternDifferentAddressReturnsDifferentNode()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);
        var root = EtlStackTable.RootForProcess(process);

        var a = model.Stacks.Intern(root, UserAddress, process, model);
        var b = model.Stacks.Intern(root, UserAddress + 1, process, model);

        Assert.AreNotEqual(a, b);
        Assert.AreEqual(2, model.Stacks.Count);
    }

    [TestMethod]
    public void InternDifferentParentReturnsDifferentNodeButSameCodeAddress()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);
        var root = EtlStackTable.RootForProcess(process);

        var caller1 = model.Stacks.Intern(root, UserAddress, process, model);
        var caller2 = model.Stacks.Intern(root, UserAddress + 0x100, process, model);
        var leaf1 = model.Stacks.Intern(caller1, UserAddress + 0x200, process, model);
        var leaf2 = model.Stacks.Intern(caller2, UserAddress + 0x200, process, model);

        Assert.AreNotEqual(leaf1, leaf2);
        Assert.AreEqual(model.Stacks.CodeAddress(leaf1), model.Stacks.CodeAddress(leaf2));
    }

    [TestMethod]
    public void ParentAndCodeAddressRoundTrip()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);
        var root = EtlStackTable.RootForProcess(process);

        var caller = model.Stacks.Intern(root, UserAddress, process, model);
        var callee = model.Stacks.Intern(caller, UserAddress + 0x40, process, model);

        Assert.AreEqual(root, model.Stacks.Parent(caller));
        Assert.AreEqual(caller, model.Stacks.Parent(callee));
        Assert.AreEqual(UserAddress, model.CodeAddresses.Address[model.Stacks.CodeAddress(caller)]);
        Assert.AreEqual(UserAddress + 0x40, model.CodeAddresses.Address[model.Stacks.CodeAddress(callee)]);
        Assert.AreEqual(process.Index, model.CodeAddresses.ProcessIndex[model.Stacks.CodeAddress(callee)]);
    }

    [TestMethod]
    public void SameUserAddressInTwoProcessesYieldsDifferentNodesAndCodeAddresses()
    {
        var model = new EtlTraceModel();
        var p1 = model.GetOrCreateProcess(100, 10);
        var p2 = model.GetOrCreateProcess(200, 10);

        var n1 = model.Stacks.Intern(EtlStackTable.RootForProcess(p1), UserAddress, p1, model);
        var n2 = model.Stacks.Intern(EtlStackTable.RootForProcess(p2), UserAddress, p2, model);

        Assert.AreNotEqual(n1, n2);
        var c1 = model.Stacks.CodeAddress(n1);
        var c2 = model.Stacks.CodeAddress(n2);
        Assert.AreNotEqual(c1, c2);
        Assert.AreEqual(p1.Index, model.CodeAddresses.ProcessIndex[c1]);
        Assert.AreEqual(p2.Index, model.CodeAddresses.ProcessIndex[c2]);
    }

    [TestMethod]
    public void KernelAddressIsOwnedByKernelProcessWhateverTheSamplingProcess()
    {
        var model = new EtlTraceModel { PointerSize = 8 };
        var p1 = model.GetOrCreateProcess(100, 10);
        var p2 = model.GetOrCreateProcess(200, 10);

        var n1 = model.Stacks.Intern(EtlStackTable.RootForProcess(p1), KernelAddress, p1, model);
        var n2 = model.Stacks.Intern(EtlStackTable.RootForProcess(p2), KernelAddress, p2, model);

        // Nodes stay per process (different roots), but the code address is the kernel's, shared by both.
        Assert.AreNotEqual(n1, n2);
        var c1 = model.Stacks.CodeAddress(n1);
        var c2 = model.Stacks.CodeAddress(n2);
        Assert.AreEqual(c1, c2);
        Assert.AreEqual(0, model.KernelProcess.ProcessId);
        Assert.AreEqual(model.KernelProcess.Index, model.CodeAddresses.ProcessIndex[c1]);
        Assert.IsFalse(p1.CodeAddressByAddress.ContainsKey(KernelAddress));
        Assert.IsFalse(p2.CodeAddressByAddress.ContainsKey(KernelAddress));
        Assert.IsTrue(model.KernelProcess.CodeAddressByAddress.ContainsKey(KernelAddress));
    }

    [TestMethod]
    public void KernelAddressBoundaryDependsOnPointerSize()
    {
        Assert.IsTrue(EtlTraceModel.IsKernelAddress(0xFFFF800000000000UL, 8));
        Assert.IsFalse(EtlTraceModel.IsKernelAddress(0xFFFF7FFFFFFFFFFFUL, 8));
        Assert.IsTrue(EtlTraceModel.IsKernelAddress(0x80000000UL, 4));
        Assert.IsFalse(EtlTraceModel.IsKernelAddress(0x7FFFFFFFUL, 4));
    }

    [TestMethod]
    public void AddressJustBelowKernelBoundaryIsOwnedBySamplingProcess()
    {
        var model = new EtlTraceModel { PointerSize = 8 };
        var process = model.GetOrCreateProcess(100, 10);

        var node = model.Stacks.Intern(EtlStackTable.RootForProcess(process), 0xFFFF7FFFFFFFFFFFUL, process, model);

        Assert.AreEqual(process.Index, model.CodeAddresses.ProcessIndex[model.Stacks.CodeAddress(node)]);
    }

    [TestMethod]
    public void ManyDistinctPairsSurviveTableGrowth()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);
        var root = EtlStackTable.RootForProcess(process);
        var stacks = model.Stacks;

        // 1000 root frames, then 200 frames below each: 201,000 distinct (parent, address) pairs, well past the
        // initial capacity, with the same aligned addresses reused under many parents.
        const int Bases = 1000;
        const int Total = 200_000;
        var bases = new int[Bases];
        for (var i = 0; i < Bases; i++)
        {
            bases[i] = stacks.Intern(root, UserAddress + (ulong)i * 16, process, model);
        }

        var nodes = new int[Total];
        for (var i = 0; i < Total; i++)
        {
            nodes[i] = stacks.Intern(bases[i % Bases], ChildAddress(i, Bases), process, model);
        }

        Assert.AreEqual(Bases + Total, stacks.Count);

        for (var i = 0; i < Bases; i++)
        {
            Assert.AreEqual(bases[i], stacks.Intern(root, UserAddress + (ulong)i * 16, process, model));
        }

        for (var i = 0; i < Total; i++)
        {
            var node = stacks.Intern(bases[i % Bases], ChildAddress(i, Bases), process, model);
            if (node != nodes[i])
            {
                Assert.Fail($"Pair {i} re-interned as node {node}, expected {nodes[i]}.");
            }

            if (stacks.Parent(node) != bases[i % Bases])
            {
                Assert.Fail($"Node {node} has parent {stacks.Parent(node)}, expected {bases[i % Bases]}.");
            }
        }

        Assert.AreEqual(Bases + Total, stacks.Count);
        Assert.AreEqual(Total, new HashSet<int>(nodes).Count);

        // One code address per distinct address of the process.
        Assert.AreEqual(Bases + Total / Bases, model.CodeAddresses.Count);
    }

    private static ulong ChildAddress(int i, int bases) => UserAddress + 0x100000 + (ulong)(i / bases) * 16;
}
