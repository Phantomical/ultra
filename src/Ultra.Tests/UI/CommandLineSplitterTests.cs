// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.UI.Services;

namespace Ultra.Tests.UI;

[TestClass]
public class CommandLineSplitterTests
{
    [TestMethod]
    [DataRow("", new string[0])]
    [DataRow("   ", new string[0])]
    [DataRow("a b  c", new[] { "a", "b", "c" })]
    [DataRow("\"a b\" c", new[] { "a b", "c" })]
    [DataRow("-popupwindow -force-d3d11", new[] { "-popupwindow", "-force-d3d11" })]
    [DataRow("--path=\"C:\\Program Files\\Game\" x", new[] { "--path=C:\\Program Files\\Game", "x" })]
    [DataRow("C:\\dir\\ file", new[] { "C:\\dir\\", "file" })]
    [DataRow("\"C:\\dir\\\\\" file", new[] { "C:\\dir\\", "file" })]
    [DataRow("a\\\"b", new[] { "a\"b" })]
    [DataRow("\"a \"\"quoted\"\" b\"", new[] { "a \"quoted\" b" })]
    [DataRow("\"\" x", new[] { "", "x" })]
    [DataRow("a\tb\r\nc", new[] { "a", "b", "c" })]
    public void SplitsLikeWindowsPrograms(string commandLine, string[] expected)
    {
        CollectionAssert.AreEqual(expected, CommandLineSplitter.Split(commandLine));
    }

    [TestMethod]
    public void NullIsNoArguments()
    {
        Assert.AreEqual(0, CommandLineSplitter.Split(null).Count);
    }
}
