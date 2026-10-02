namespace SeatRes.Api;

public sealed class SeatResOptions
{
    public const string Section = "SeatRes";

    /// <summary>How long a hold protects a seat.</summary>
    public int HoldSeconds { get; set; } = 300;
    /// <summary>Extra time a seat stays pinned while its payment is processing.</summary>
    public int PayGraceSeconds { get; set; } = 60;
    public int MaxSeatsPerShow { get; set; } = 20_000;
    public int MaxSeatsPerRequest { get; set; } = 64;
    /// <summary>₹1 crore (10^9 paise) per seat: far above any real ticket, far below bigint overflow for a full request.</summary>
    public long MaxPricePaise { get; set; } = 1_000_000_000;

    public bool FastPathEnabled { get; set; } = true;
    public int TakenCacheMillis { get; set; } = 1000;
    public int GateTimeoutMillis { get; set; } = 5000;

    public int AdmissionPermits { get; set; } = 1024;
    public int AdmissionQueue { get; set; } = 20_000;

    public int GatewayTimeoutMillis { get; set; } = 5000;
    public bool BackgroundEnabled { get; set; } = true;

    public TimeSpan Hold => TimeSpan.FromSeconds(HoldSeconds);
    public TimeSpan PayGrace => TimeSpan.FromSeconds(PayGraceSeconds);
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public string JwtKey { get; set; } = "";
    public string AdminKey { get; set; } = "";
    public int TokenHours { get; set; } = 24;
}

public sealed class GatewayOptions
{
    public const string Section = "Gateway";
    public int LatencyMillis { get; set; } = 30;
    public double DeclineRate { get; set; }
    public double UnknownRate { get; set; }
    /// <summary>Honour the X-Sim-Gateway request header (decline / unknown / slow) so bursts can exercise failure paths.</summary>
    public bool AllowClientOverride { get; set; } = true;
}
