using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class ServiceFactoryTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public void ScrapingLetterboxdService_ImplementsInterface()
    {
        var service = new ScrapingLetterboxdService(TestLogger);
        Assert.IsAssignableFrom<ILetterboxdService>(service);
        service.Dispose();
    }

    [Fact]
    public void LetterboxdApiClient_ImplementsInterface()
    {
        var client = new LetterboxdApiClient(TestLogger);
        Assert.IsAssignableFrom<ILetterboxdService>(client);
        client.Dispose();
    }

    [Fact]
    public void ILetterboxdService_IsDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ILetterboxdService)));
    }

    [Fact]
    public void LetterboxdApiConstants_HasRequiredFields()
    {
        Assert.False(string.IsNullOrEmpty(LetterboxdApiConstants.BaseUrl));
        Assert.False(string.IsNullOrEmpty(LetterboxdApiConstants.ApiKey));
        Assert.False(string.IsNullOrEmpty(LetterboxdApiConstants.ApiSecret));
        Assert.StartsWith("https://", LetterboxdApiConstants.BaseUrl);
        Assert.Equal(64, LetterboxdApiConstants.ApiKey.Length);
        Assert.Equal(64, LetterboxdApiConstants.ApiSecret.Length);
    }
}
