using System;

namespace Wabbajack.DTOs.Interventions;

/// <summary>
/// Exception thrown when a manual download is required
/// </summary>
public class ManualDownloadRequiredException : Exception
{
    /// <summary>The archive that requires manual download. Null only when thrown via legacy code paths.</summary>
    public Archive? Archive { get; }

    public ManualDownloadRequiredException(Archive archive, string message) : base(message)
    {
        Archive = archive;
    }

    public ManualDownloadRequiredException(string message) : base(message)
    {
    }

    public ManualDownloadRequiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when any user intervention is required
/// </summary>
public class UserInterventionRequiredException : Exception
{
    public UserInterventionRequiredException(string message) : base(message)
    {
    }
    
    public UserInterventionRequiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
