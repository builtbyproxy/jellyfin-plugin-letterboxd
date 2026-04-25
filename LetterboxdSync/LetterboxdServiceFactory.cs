using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public static class LetterboxdServiceFactory
{
    public static async Task<ILetterboxdService> CreateAuthenticatedAsync(
        string username, string password, string? rawCookies, ILogger logger, string? userAgent = null)
    {
        try
        {
            var apiClient = new LetterboxdApiClient(logger);
            await apiClient.AuthenticateAsync(username, password).ConfigureAwait(false);
            logger.LogInformation("Using official Letterboxd API for {Username}", username);
            return apiClient;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Official API auth failed for {Username}, falling back to scraping: {Message}",
                username, ex.Message);
        }

        var scraping = new ScrapingLetterboxdService(logger, userAgent);
        await scraping.AuthenticateAsync(username, password, rawCookies).ConfigureAwait(false);
        logger.LogInformation("Using web scraping fallback for {Username}", username);
        return scraping;
    }
}
