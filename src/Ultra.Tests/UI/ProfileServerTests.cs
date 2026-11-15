// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Net;
using Ultra.UI.Services;

namespace Ultra.Tests.UI;

/// <summary>
/// Tests for <see cref="ProfileServer"/>, which serves profiles to the Firefox Profiler web application.
/// </summary>
[TestClass]
public class ProfileServerTests
{
    private string _tempDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ultra-profile-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [TestMethod]
    public async Task ServesARegisteredFileWithCorsHeaders()
    {
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        var file = Path.Combine(_tempDirectory, "ultra_app_2026.json.gz");
        await File.WriteAllBytesAsync(file, bytes);

        using var server = new ProfileServer();
        server.Start();
        var url = server.Register(file);

        Assert.AreEqual("127.0.0.1", url.Host);
        Assert.AreEqual(server.Port, url.Port);
        StringAssert.EndsWith(url.AbsolutePath, "/ultra_app_2026.json.gz");

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Origin", "https://profiler.firefox.com");
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        CollectionAssert.AreEqual(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task RegisteringTheSameFileTwiceGivesTheSameUrl()
    {
        var file = Path.Combine(_tempDirectory, "a.json.gz");
        await File.WriteAllTextAsync(file, "x");

        using var server = new ProfileServer();
        server.Start();

        Assert.AreEqual(server.Register(file), server.Register(file));
        Assert.AreNotEqual(server.Register(file), server.Register(Path.Combine(_tempDirectory, "b.json.gz")));
    }

    [TestMethod]
    public async Task UnknownOrUnregisteredTokensAreNotFound()
    {
        var file = Path.Combine(_tempDirectory, "a.json.gz");
        await File.WriteAllTextAsync(file, "x");

        using var server = new ProfileServer();
        server.Start();
        var url = server.Register(file);

        using var client = new HttpClient();
        var guessed = new Uri($"http://127.0.0.1:{server.Port}/profiles/{new string('0', 32)}/a.json.gz");
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(guessed)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/{Path.GetFileName(file)}"))).StatusCode);

        server.Unregister(file);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
    }

    [TestMethod]
    public async Task AnswersPreflightRequestsForPrivateNetworkAccess()
    {
        using var server = new ProfileServer();
        server.Start();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri($"http://127.0.0.1:{server.Port}/profiles/x/y.json.gz"));
        request.Headers.Add("Origin", "https://profiler.firefox.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Private-Network", "true");
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
        Assert.AreEqual("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [TestMethod]
    public async Task HeadReturnsTheLengthWithoutTheContent()
    {
        var file = Path.Combine(_tempDirectory, "a.json.gz");
        await File.WriteAllBytesAsync(file, new byte[1234]);

        using var server = new ProfileServer();
        server.Start();

        using var client = new HttpClient();
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Register(file)));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1234, response.Content.Headers.ContentLength);
    }

    [TestMethod]
    public void RegisterRequiresAStartedServer()
    {
        using var server = new ProfileServer();
        Assert.ThrowsExactly<InvalidOperationException>(() => server.Register(Path.Combine(_tempDirectory, "a.json.gz")));
    }

    [TestMethod]
    public void FirefoxProfilerLinkEmbedsTheEscapedProfileUrl()
    {
        var link = FirefoxProfilerLinks.BuildFromUrl(new Uri("http://127.0.0.1:1234/profiles/abc/my profile.json.gz"), "KSP - Capture 1");

        Assert.AreEqual(
            "https://profiler.firefox.com/from-url/http%3A%2F%2F127.0.0.1%3A1234%2Fprofiles%2Fabc%2Fmy%2520profile.json.gz/?profileName=KSP%20-%20Capture%201",
            link.AbsoluteUri);
    }

    [TestMethod]
    public void FirefoxProfilerLinkWithoutAName()
    {
        var link = FirefoxProfilerLinks.BuildFromUrl(new Uri("http://127.0.0.1:1/profiles/abc/a.json.gz"));

        Assert.AreEqual("https://profiler.firefox.com/from-url/http%3A%2F%2F127.0.0.1%3A1%2Fprofiles%2Fabc%2Fa.json.gz/", link.AbsoluteUri);
    }
}
