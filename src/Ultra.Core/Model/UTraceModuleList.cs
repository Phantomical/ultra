// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UTraceModule"/> instances.
/// </summary>
public sealed class UTraceModuleList : UGenericList<UTraceModule>
{
    private UnsafeDictionary<string, UTraceModuleFileIndex> _mapModulePathToIndex = new();
    private UnsafeList<UTraceModuleFile> _moduleFiles = new();
    private UnsafeDictionary<long, int> _mapModuleIDToManagedModule = new();
    private UnsafeDictionary<ulong, int> _mapModuleAddressToLoadedModule = new();
    private UnsafeList<UAddressRange> _loadedModuleAddressRanges = new();
    private bool _loadedModuleAddressRangesSorted = true;

    /// <summary>
    /// Gets or creates a module file based on the file path.
    /// </summary>
    /// <param name="filePath">The file path of the module.</param>
    /// <returns>The <see cref="UTraceModuleFile"/> instance.</returns>
    public UTraceModuleFile GetOrCreateModuleFile(string filePath)
    {
        if (_mapModulePathToIndex.TryGetValue(filePath, out var index))
        {
            return _moduleFiles[index];
        }

        var moduleFile = new UTraceModuleFile(filePath);
        index = new UTraceModuleFileIndex(_moduleFiles.Count);
        moduleFile.Index = index;
        _mapModulePathToIndex.Add(filePath, index);
        _moduleFiles.Add(moduleFile);
        return moduleFile;
    }

    /// <summary>
    /// Gets the module files in the list.
    /// </summary>
    public ReadOnlySpan<UTraceModuleFile> ModuleFiles => _moduleFiles.AsSpan();

    /// <summary>
    /// Tries to find a managed module by its module ID.
    /// </summary>
    /// <param name="moduleID">The module ID.</param>
    /// <param name="managedModule">The managed module found, if any.</param>
    /// <returns>True if the managed module was found, otherwise false.</returns>
    public bool TryGetManagedModule(long moduleID, [NotNullWhen(true)] out UTraceManagedModule? managedModule)
    {
        if (_mapModuleIDToManagedModule.TryGetValue(moduleID, out var managedModuleIndex))
        {
            managedModule = (UTraceManagedModule)List[managedModuleIndex];
            return true;
        }

        managedModule = null;
        return false;
    }

    /// <summary>
    /// Gets or creates a loaded module based on the file path, base address, and code size.
    /// </summary>
    /// <param name="baseAddress">The base address of the module.</param>
    /// <param name="codeSize">The size of the code.</param>
    /// <param name="moduleFilePath">The file path of the module.</param>
    /// <returns>The <see cref="UTraceModule"/> instance.</returns>
    public UTraceModule GetOrCreateNativeModule(UAddress baseAddress, USize codeSize, string moduleFilePath)
    {
        if (_mapModuleAddressToLoadedModule.TryGetValue(baseAddress, out var loadedModuleIndex))
        {
            return List[loadedModuleIndex];
        }

        var moduleFile = GetOrCreateModuleFile(moduleFilePath);
        var loadedModule = new UTraceNativeModule(moduleFile, baseAddress, codeSize);
        loadedModuleIndex = List.Count;
        _mapModuleAddressToLoadedModule.Add(baseAddress, loadedModuleIndex);
        List.Add(loadedModule);

        _loadedModuleAddressRanges.Add(new(baseAddress, baseAddress + codeSize, loadedModuleIndex));
        // Sorting (required by the binary search in TryFindNativeModuleByAddress) is deferred to the first lookup
        _loadedModuleAddressRangesSorted = false;

        return loadedModule;
    }

    /// <summary>
    /// Ensures that the native module address ranges consulted by <see cref="TryFindNativeModuleByAddress"/>
    /// are sorted, performing the sort immediately if it has not happened yet.
    /// </summary>
    /// <remarks>
    /// <see cref="TryFindNativeModuleByAddress"/> otherwise sorts these ranges lazily, on its first call
    /// after a new module was added. That lazy sort is not safe to trigger from multiple threads
    /// concurrently (two threads could sort the same span at the same time, or one could binary-search a
    /// half-sorted array). Callers that call <see cref="TryFindNativeModuleByAddress"/> from more than one
    /// thread for the same process must call this method once, from a single thread, after all native
    /// modules have been added and before any concurrent use of <see cref="TryFindNativeModuleByAddress"/>.
    /// </remarks>
    public void EnsureNativeModuleAddressRangesSorted()
    {
        if (_loadedModuleAddressRangesSorted)
        {
            return;
        }

        _loadedModuleAddressRanges.AsSpan().SortByRef(new UAddressRangeComparer());
        _loadedModuleAddressRangesSorted = true;
    }

    /// <summary>
    /// Tries to find a module by its address.
    /// </summary>
    /// <param name="address">The address of the module.</param>
    /// <param name="module">The module found, if any.</param>
    /// <returns>True if the module was found, otherwise false.</returns>
    /// <remarks>
    /// This performs a lazy sort of the address ranges on its first call after a new module was added; see
    /// <see cref="EnsureNativeModuleAddressRangesSorted"/> for the thread-safety caveat this implies when
    /// this method is called concurrently from multiple threads.
    /// </remarks>
    public bool TryFindNativeModuleByAddress(UAddress address, [NotNullWhen(true)] out UTraceNativeModule? module)
    {
        EnsureNativeModuleAddressRangesSorted();

        var ranges = _loadedModuleAddressRanges.AsSpan();
        var comparer = new UAddressRangeFinder(address);
        var index = ranges.BinarySearch(comparer);
        if (index >= 0)
        {
            module = (UTraceNativeModule)List[ranges[index].Index];
            return true;
        }
        module = null;
        return false;
    }

    /// <summary>
    /// Gets or creates a managed module based on the module ID, assembly ID, file path, base address, and code size.
    /// </summary>
    /// <param name="moduleID">The module ID.</param>
    /// <param name="assemblyId">The assembly ID.</param>
    /// <param name="moduleFilePath">The file path of the module.</param>
    /// <returns>The <see cref="UTraceManagedModule"/> instance.</returns>
    public UTraceManagedModule GetOrCreateManagedModule(long moduleID, long assemblyId, string moduleFilePath)
    {
        if (_mapModuleIDToManagedModule.TryGetValue(moduleID, out var managedModuleIndex))
        {
            return (UTraceManagedModule)List[managedModuleIndex];
        }
        var moduleFile = GetOrCreateModuleFile(moduleFilePath);
        var managedModule = new UTraceManagedModule(moduleID, assemblyId, moduleFile);
        _mapModuleIDToManagedModule.Add(moduleID, List.Count);
        List.Add(managedModule);
        return managedModule;
    }
}