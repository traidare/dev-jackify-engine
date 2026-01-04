using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.DTOs.Logins;
using Wabbajack.Networking.NexusApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.Services.OSIntegrated.TokenProviders;

public class NexusApiTokenProvider : EncryptedJsonTokenProvider<NexusOAuthState>, IAuthInfo
{
    public NexusApiTokenProvider(ILogger<NexusApiTokenProvider> logger, DTOSerializer dtos) : base(logger, dtos,
        "nexus-oauth-info")
    {
    }

    // NOTE: GUI writes encrypted files (~/.config/jackify/nexus-oauth.json) with AES-GCM encryption
    // The engine does NOT support AES-GCM decryption - it only supports:
    // 1. ProtectedData (TripleDES) for encrypted files in ~/Jackify/encrypted/
    // 2. Plain JSON (not encrypted)
    // 
    // SOLUTION: GUI should decrypt and pass OAuth via NEXUS_OAUTH_INFO environment variable
    // This is already implemented and working in NexusApi.GetAuthInfo()
    
    public override async ValueTask<NexusOAuthState?> Get()
    {
        // Use base class behavior (encrypted file or environment variable)
        // GUI should pass OAuth via NEXUS_OAUTH_INFO env var (already supported)
        return await base.Get();
    }

    public override bool HaveToken()
    {
        return base.HaveToken();
    }
}