namespace CSVM.Net;

/// <summary>
/// What a host hands a joining guest before either flies: the master seed <c>Utils.Rng.Reset</c>
/// takes, and the host's session clock at send. The guest seeds its streams before its roster
/// builds, so both peers draw the same liveries, spawn walk and dice. The clock opens its
/// <see cref="NetClockSlew"/>. No layout is declared here; the bytes are the vocabulary's.
/// </summary>
public readonly record struct NetHandshake(ulong Seed, double HostClock);
