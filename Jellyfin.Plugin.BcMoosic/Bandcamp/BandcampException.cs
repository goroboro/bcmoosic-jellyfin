using System;

namespace Jellyfin.Plugin.BcMoosic.Bandcamp;

/// <summary>Thrown when a Bandcamp operation fails with a known error condition.</summary>
public class BandcampException : Exception
{
    public BandcampException(string message) : base(message) { }
    public BandcampException(string message, Exception inner) : base(message, inner) { }
}
