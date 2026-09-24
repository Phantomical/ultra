// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;

namespace Ultra.Core.Etw;

/// <summary>
/// The interned call-stack tree of an <see cref="EtlTraceModel"/>: a node is a (parent, address) pair; the root of
/// a stack is a virtual parent below <c>0</c> that identifies the process (see <see cref="RootForProcess"/>), so
/// identical stacks of the same process share nodes and stacks of different processes never do.
/// </summary>
/// <remarks>
/// Interning a frame is the hottest operation of the whole conversion (one per frame of every stack walked),
/// so nodes are keyed by the raw address rather than by a code address looked up first: a node resolves its
/// code address (see <see cref="EtlCodeAddressTable"/>) once, when it is created. The index is an open-addressing
/// hash table whose slots pack the key and the node in 16 bytes, so a lookup usually touches one cache line.
/// </remarks>
internal sealed class EtlStackTable
{
    private struct Slot
    {
        public ulong Address;
        public int Parent;

        /// <summary>The node, or -1 for an empty slot.</summary>
        public int Node;
    }

    private int[] _parents = new int[1 << 16];
    private int[] _codeAddresses = new int[1 << 16];
    private Slot[] _slots = new Slot[1 << 17];
    private int _count;

    public EtlStackTable()
    {
        Array.Fill(_slots, new Slot { Node = -1 });
    }

    /// <summary>The number of nodes.</summary>
    public int Count => _count;

    /// <summary>The parent node of <paramref name="node"/>, negative for a root frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Parent(int node) => _parents[node];

    /// <summary>The code address of <paramref name="node"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CodeAddress(int node) => _codeAddresses[node];

    /// <summary>The virtual parent of the root frames of the stacks of <paramref name="process"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RootForProcess(EtlProcess process) => -1 - process.Index;

    /// <summary>
    /// Gets the node for <paramref name="address"/> called from <paramref name="parent"/>, creating it - and its code
    /// address in <paramref name="process"/> (or the kernel process for a kernel-mode address) - if needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Intern(int parent, ulong address, EtlProcess process, EtlTraceModel model)
    {
        var slots = _slots;
        var mask = slots.Length - 1;
        var i = (int)Hash(parent, address) & mask;
        while (true)
        {
            ref var slot = ref slots[i];
            if (slot.Node < 0)
            {
                break;
            }

            if (slot.Address == address && slot.Parent == parent)
            {
                return slot.Node;
            }

            i = (i + 1) & mask;
        }

        return Add(parent, address, process, model, i);
    }

    private int Add(int parent, ulong address, EtlProcess process, EtlTraceModel model, int slotIndex)
    {
        var owner = EtlTraceModel.IsKernelAddress(address, model.PointerSize) ? model.KernelProcess : process;
        var codeAddress = model.CodeAddresses.GetOrCreate(owner, address);

        var node = _count;
        if (node == _parents.Length)
        {
            Array.Resize(ref _parents, node * 2);
            Array.Resize(ref _codeAddresses, node * 2);
        }

        _parents[node] = parent;
        _codeAddresses[node] = codeAddress;
        _count = node + 1;

        ref var slot = ref _slots[slotIndex];
        slot.Address = address;
        slot.Parent = parent;
        slot.Node = node;

        // Keep the load factor under 1/2.
        if (_count * 2 > _slots.Length)
        {
            Grow();
        }

        return node;
    }

    private void Grow()
    {
        var oldSlots = _slots;
        var slots = new Slot[oldSlots.Length * 2];
        Array.Fill(slots, new Slot { Node = -1 });
        var mask = slots.Length - 1;
        foreach (ref var old in oldSlots.AsSpan())
        {
            if (old.Node < 0)
            {
                continue;
            }

            var i = (int)Hash(old.Parent, old.Address) & mask;
            while (slots[i].Node >= 0)
            {
                i = (i + 1) & mask;
            }

            slots[i] = old;
        }

        _slots = slots;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(int parent, ulong address)
    {
        // A cheap mix of both halves of the address with the parent: addresses are mostly aligned and cluster in
        // the same few modules, so the low bits alone hash poorly.
        var h = (address ^ ((ulong)(uint)parent << 20)) * 0x9E3779B97F4A7C15UL;
        return (uint)(h >> 32) ^ (uint)h;
    }
}
