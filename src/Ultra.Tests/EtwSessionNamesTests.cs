// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtwSessionNames"/>, which lets ultra recognize and stop the ETW sessions that an ultra process
/// that died left recording.
/// </summary>
[TestClass]
public class EtwSessionNamesTests
{
    private const string BaseName = "ultra_KSP_x64_2026-11-08_19_23_46_pid_60596";

    [TestMethod]
    public void CreateRecordsTheOwnerProcessId()
    {
        Assert.AreEqual($"{BaseName}-ultra1234-kernel", EtwSessionNames.Create(BaseName, "kernel", 1234));
        Assert.AreEqual($"{BaseName}-ultra{Environment.ProcessId}-user", EtwSessionNames.Create(BaseName, "user"));
    }

    [TestMethod]
    [DataRow("ultra")]
    [DataRow("dotnet")]
    [DataRow("Ultra")]
    public void SessionOfARunningUltraIsOwned(string processName)
    {
        var name = EtwSessionNames.Create(BaseName, "kernel", 1234);
        Assert.AreEqual(EtwSessionNames.Ownership.Owned, EtwSessionNames.Classify(name, pid => pid == 1234 ? processName : null, NotAsked));
    }

    [TestMethod]
    public void SessionOfAnExitedUltraIsOrphaned()
    {
        foreach (var kind in new[] { "kernel", "user", "rundown" })
        {
            var name = EtwSessionNames.Create(BaseName, kind, 1234);
            Assert.AreEqual(EtwSessionNames.Ownership.Orphaned, EtwSessionNames.Classify(name, _ => null, NotAsked), kind);
        }
    }

    [TestMethod]
    public void SessionWhoseOwnerPidWasReusedByAnotherProgramIsOrphaned()
    {
        var name = EtwSessionNames.Create(BaseName, "kernel", 1234);
        Assert.AreEqual(EtwSessionNames.Ownership.Orphaned, EtwSessionNames.Classify(name, _ => "notepad", NotAsked));
    }

    [TestMethod]
    public void SessionWithACustomOutputNameIsRecognizedByItsOwnerMarker()
    {
        // `-o` replaces the ultra_ prefix of the base name with the user's own name.
        var name = EtwSessionNames.Create("my-run_2026-11-08_19_23_46", "user", 1234);
        Assert.AreEqual(EtwSessionNames.Ownership.Orphaned, EtwSessionNames.Classify(name, _ => null, NotAsked));
    }

    [TestMethod]
    public void LegacySessionIsOrphanedOnlyWhenNoOtherUltraIsRunning()
    {
        // The name of a session that leaked from an ultra version that did not record its pid.
        var name = $"{BaseName}-kernel";
        Assert.AreEqual(EtwSessionNames.Ownership.Orphaned, EtwSessionNames.Classify(name, _ => null, () => false));
        Assert.AreEqual(EtwSessionNames.Ownership.LegacyUnknown, EtwSessionNames.Classify(name, _ => null, () => true));
    }

    [TestMethod]
    [DataRow("NT Kernel Logger")]
    [DataRow("Eventlog-Security")]
    [DataRow("SomeTool-kernel")]
    [DataRow("ultra_something")]
    [DataRow("prefix-ultra12-kernel-suffix")]
    [DataRow("prefix-ultraX-kernel")]
    public void OtherSessionsAreNotUltras(string name)
    {
        Assert.AreEqual(EtwSessionNames.Ownership.NotUltra, EtwSessionNames.Classify(name, _ => null, () => false));
    }

    private static bool NotAsked()
    {
        Assert.Fail("Whether another ultra is running only matters for legacy session names");
        return false;
    }
}
