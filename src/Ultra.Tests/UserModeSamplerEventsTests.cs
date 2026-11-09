// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw.UserMode;

namespace Ultra.Tests;

/// <summary>Round trips of the payloads the user-mode sampler writes and <c>EtlTraceReader</c> reads back.</summary>
[TestClass]
public class UserModeSamplerEventsTests
{
    [TestMethod]
    public void SampleRoundTrips()
    {
        var buffer = new byte[UserModeSamplerEvents.MaxEventSize];
        ulong[] frames = [0x7FF6_1234_5678, 0x2000_1000, 0x7FFF_0000_0010];
        var length = UserModeSamplerEvents.EncodeSample(buffer, 1234, 5678, UserModeSamplerEvents.SampleFlagTruncated, frames);

        var (processId, threadId, flags) = UserModeSamplerEvents.DecodeSample(buffer.AsSpan(0, length), out var decoded);

        Assert.AreEqual(12 + 3 * 8, length);
        Assert.AreEqual(1234, processId);
        Assert.AreEqual(5678, threadId);
        Assert.AreEqual(UserModeSamplerEvents.SampleFlagTruncated, flags);
        CollectionAssert.AreEqual(frames, decoded.ToArray());
    }

    [TestMethod]
    public void SampleFramesAreCappedAndTruncatedPayloadsAreSafe()
    {
        var buffer = new byte[UserModeSamplerEvents.MaxEventSize];
        var frames = new ulong[UserModeSamplerEvents.MaxFrames + 10];
        var length = UserModeSamplerEvents.EncodeSample(buffer, 1, 2, 0, frames);
        Assert.AreEqual(12 + UserModeSamplerEvents.MaxFrames * 8, length);

        // A payload shorter than its frame count says decodes the frames it holds.
        UserModeSamplerEvents.DecodeSample(buffer.AsSpan(0, 12 + 2 * 8 + 3), out var decoded);
        Assert.AreEqual(2, decoded.Length);
    }

    [TestMethod]
    public void ModuleLoadRoundTrips()
    {
        var buffer = new byte[UserModeSamplerEvents.MaxEventSize];
        var module = new UserModeSamplerEvents.ModuleLoad(42, 0x7FFE_5100_0000, 0x48_0000, 0x12345, 0x6ACC4F02, UserModeSamplerEvents.FlagRundown, @"E:\KSP\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll");
        var length = UserModeSamplerEvents.EncodeModuleLoad(buffer, module);

        Assert.AreEqual(module, UserModeSamplerEvents.DecodeModuleLoad(buffer.AsSpan(0, length)));
    }

    [TestMethod]
    public void ProcessAndThreadInfoRoundTrip()
    {
        var buffer = new byte[UserModeSamplerEvents.MaxEventSize];
        var process = new UserModeSamplerEvents.ProcessInfo(42, 1.0f, @"E:\KSP\KSP_x64.exe");
        var length = UserModeSamplerEvents.EncodeProcessInfo(buffer, process);
        Assert.AreEqual(process, UserModeSamplerEvents.DecodeProcessInfo(buffer.AsSpan(0, length)));

        var thread = new UserModeSamplerEvents.ThreadStart(42, 7, 0, "Job.Worker 3");
        length = UserModeSamplerEvents.EncodeThreadStart(buffer, thread);
        Assert.AreEqual(thread, UserModeSamplerEvents.DecodeThreadStart(buffer.AsSpan(0, length)));

        length = UserModeSamplerEvents.EncodeThreadStop(buffer, 42, 7);
        Assert.AreEqual((42, 7), UserModeSamplerEvents.DecodeThreadStop(buffer.AsSpan(0, length)));

        length = UserModeSamplerEvents.EncodeModuleUnload(buffer, 42, 0x1000);
        Assert.AreEqual((42, 0x1000UL), UserModeSamplerEvents.DecodeModuleUnload(buffer.AsSpan(0, length)));
    }
}
