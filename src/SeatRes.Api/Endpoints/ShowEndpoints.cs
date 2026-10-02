using Microsoft.Extensions.Options;
using SeatRes.Api.Auth;
using SeatRes.Api.Data;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Endpoints;

public sealed record CreateShowRequest(string? Name, string[]? Seats, long? PricePaise, int? PerUserLimit);

public sealed record SeatDto(string Label, string Status);
public sealed record CountsDto(int Available, int Held, int Confirmed);
public sealed record LedgerDto(long ChargedPaise, long RefundedPaise, long NetPaise, long ExpectedNetPaise);
public sealed record ShowDto(
    Guid Id, string Name, long PricePaise, int PerUserLimit, int TotalSeats,
    CountsDto Counts, bool Reconciled, LedgerDto Ledger, IReadOnlyList<SeatDto> Seats, DateTime ServerTime);

public static class ShowEndpoints
{
    public static void MapShowEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/shows", async (CreateShowRequest body, ShowStore store, ShowCatalog catalog,
            IOptions<SeatResOptions> opt, TimeProvider time, CancellationToken ct) =>
        {
            if (Validate(body, opt.Value) is { } error) return error;
            var show = new ShowInfo(Guid.CreateVersion7(), body.Name!.Trim(), body.PricePaise!.Value,
                body.PerUserLimit ?? 4, body.Seats!.Length, body.Seats);
            var now = time.GetUtcNow().UtcDateTime;
            await store.CreateAsync(show, now, ct);
            catalog.Add(show);
            return ApiResult.Ok(ToDto(new ShowState(show, show.Labels.Select(l => new SeatView(l, "available")).ToList(), 0, 0), now), 201);
        }).RequireAuthorization(AuthSetup.AdminPolicy);

        app.MapGet("/shows/{id:guid}", async (Guid id, ShowStore store, ShowCatalog catalog, TimeProvider time, CancellationToken ct) =>
        {
            var show = await catalog.GetAsync(id, ct);
            if (show is null) return ApiResult.Fail(404, ErrorCodes.NotFound, "show not found");
            var now = time.GetUtcNow().UtcDateTime;
            return ApiResult.Ok(ToDto(await store.GetStateAsync(show, now, ct), now));
        });
    }

    private static ApiResult? Validate(CreateShowRequest b, SeatResOptions opt)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 100)
            return Bad("name is required (at most 100 characters)");
        if (b.PricePaise is not { } price || price < 0 || price > opt.MaxPricePaise)
            return Bad($"price_paise is required: an integer between 0 and {opt.MaxPricePaise}");
        if (b.PerUserLimit is <= 0)
            return Bad("per_user_limit must be positive");
        if (b.Seats is not { Length: > 0 } seats || seats.Length > opt.MaxSeatsPerShow)
            return Bad($"seats must contain between 1 and {opt.MaxSeatsPerShow} labels");
        if (seats.FirstOrDefault(s => !SeatLabel.IsValid(s)) is { } bad)
            return Bad($"invalid seat label '{bad}' (allowed: A-Z, a-z, 0-9, '-', up to 16 chars)");
        if (seats.Distinct(StringComparer.Ordinal).Count() != seats.Length)
            return Bad("seat labels must be unique");
        return null;
    }

    private static ApiResult Bad(string message) => ApiResult.Fail(400, ErrorCodes.BadRequest, message);

    private static ShowDto ToDto(ShowState state, DateTime now)
    {
        var s = state.Show;
        var counts = new CountsDto(state.Count("available"), state.Count("held"), state.Count("confirmed"));
        return new ShowDto(
            s.Id, s.Name, s.PricePaise, s.PerUserLimit, s.TotalSeats, counts,
            Reconciled: counts.Available + counts.Held + counts.Confirmed == s.TotalSeats && state.Seats.Count == s.TotalSeats,
            new LedgerDto(state.ChargedPaise, state.RefundedPaise, state.ChargedPaise - state.RefundedPaise,
                ExpectedNetPaise: s.PricePaise * counts.Confirmed),
            state.Seats.Select(x => new SeatDto(x.Label, x.Status)).ToList(),
            now);
    }
}
