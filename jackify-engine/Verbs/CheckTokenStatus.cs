using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.Networking.NexusApi;

namespace Wabbajack.CLI.Verbs;

public class CheckTokenStatus
{
    private readonly ILogger<CheckTokenStatus> _logger;
    private readonly NexusApi _nexusApi;

    public CheckTokenStatus(ILogger<CheckTokenStatus> logger, NexusApi nexusApi)
    {
        _logger = logger;
        _nexusApi = nexusApi;
    }

    public static VerbDefinition Definition = new("check-token-status",
        "Check OAuth token expiry status", Array.Empty<OptionDefinition>());

    public async Task<int> Run()
    {
        var expiryInfo = await _nexusApi.GetTokenExpiryInfo();
        
        if (expiryInfo == null)
        {
            _logger.LogInformation("No token configured or using API key");
            _logger.LogInformation("Note: If an install is running, this command shows the original token state, not the in-memory refreshed token.");
            return 0;
        }
        
        _logger.LogInformation("=== OAuth Token Status ===");
        _logger.LogInformation("Source: {Source}", expiryInfo.Source);
        
        // Note about in-memory state
        if (expiryInfo.Source == "NEXUS_OAUTH_INFO")
        {
            _logger.LogInformation("Note: If a refresh occurred during an install, this shows the refreshed token expiry.");
        }
        
        if (expiryInfo.ExpiresAt.HasValue)
        {
            _logger.LogInformation("Expires At: {ExpiresAt} UTC", expiryInfo.ExpiresAt.Value);
        }
        
        if (expiryInfo.TimeUntilExpiry.HasValue)
        {
            var timeUntil = expiryInfo.TimeUntilExpiry.Value;
            if (timeUntil.TotalHours >= 1.0)
            {
                _logger.LogInformation("Time Until Expiry: {Hours:F1} hours", timeUntil.TotalHours);
            }
            else if (timeUntil.TotalMinutes >= 1.0)
            {
                _logger.LogInformation("Time Until Expiry: {Minutes:F0} minutes", timeUntil.TotalMinutes);
            }
            else
            {
                _logger.LogInformation("Time Until Expiry: {Seconds:F0} seconds", timeUntil.TotalSeconds);
            }
        }
        
        if (expiryInfo.IsExpiringSoon.HasValue)
        {
            _logger.LogInformation("Is Expiring Soon (within 15 min): {IsExpiringSoon}", expiryInfo.IsExpiringSoon.Value);
        }
        
        return 0;
    }
}

