using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class ServiceRegistratorTests
{
    [Fact]
    public void RegisterServices_AddsPlaybackHandlerAsHostedService()
    {
        var services = new ServiceCollection();
        var registrator = new ServiceRegistrator();

        registrator.RegisterServices(services, null!);

        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(Microsoft.Extensions.Hosting.IHostedService), descriptor.ServiceType);
        Assert.Equal(typeof(PlaybackHandler), descriptor.ImplementationType);
    }
}

