using System.Collections.Concurrent;
using Dapper;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Data;

/// <summary>Immutable show metadata. Seat numbers are 1-based positions in the creation request.</summary>
public sealed record ShowInfo(Guid Id, string Name, long PricePaise, int PerUserLimit, int TotalSeats, string[] Labels)
{
    public IReadOnlyDictionary<string, int> SeatNoByLabel { get; } =
        Labels.Select((label, i) => (label, no: i + 1)).ToDictionary(x => x.label, x => x.no, StringComparer.Ordinal);

    public string LabelOf(int seatNo) => Labels[seatNo - 1];
}

public sealed record SeatView(string Label, string Status);

public sealed record ShowState(ShowInfo Show, IReadOnlyList<SeatView> Seats, long ChargedPaise, long RefundedPaise)
{
    public int Count(string status) => Seats.Count(s => s.Status == status);
}

public sealed class ShowStore(Db db)
{
    public Task CreateAsync(ShowInfo show, DateTime now, CancellationToken ct) => db.InTx(async (c, tx) =>
    {
        await c.ExecuteAsync(
            "INSERT INTO shows (id, name, price_paise, per_user_limit, total_seats, created_at) VALUES (@Id, @Name, @PricePaise, @PerUserLimit, @TotalSeats, @now)",
            new { show.Id, show.Name, show.PricePaise, show.PerUserLimit, show.TotalSeats, now }, tx);
        await c.ExecuteAsync(
            """
            INSERT INTO seats (show_id, seat_no, label, status)
            SELECT @Id, t.seat_no::int, t.label, 'available'
            FROM unnest(@Labels::text[]) WITH ORDINALITY AS t (label, seat_no)
            """,
            new { show.Id, show.Labels }, tx);
        return 0;
    }, ct);

    public async Task<ShowInfo?> LoadAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var head = await c.QuerySingleOrDefaultAsync<(Guid Id, string Name, long PricePaise, int PerUserLimit, int TotalSeats)>(
            "SELECT id, name, price_paise, per_user_limit, total_seats FROM shows WHERE id = @id", new { id });
        if (head.Id == Guid.Empty) return null;
        var labels = (await c.QueryAsync<string>("SELECT label FROM seats WHERE show_id = @id ORDER BY seat_no", new { id })).ToArray();
        return new ShowInfo(head.Id, head.Name, head.PricePaise, head.PerUserLimit, head.TotalSeats, labels);
    }

    /// <summary>Seat states and ledger totals from one consistent snapshot.</summary>
    public Task<ShowState> GetStateAsync(ShowInfo show, DateTime now, CancellationToken ct) => db.Read(async (c, tx) =>
    {
        var seats = (await c.QueryAsync<SeatView>(
            $"SELECT label AS Label, {SeatRules.EffectiveStatusSql} AS Status FROM seats WHERE show_id = @id ORDER BY seat_no",
            new { id = show.Id, now }, tx)).ToList();
        var (charged, refunded) = await c.QuerySingleAsync<(long, long)>(
            """
            SELECT coalesce(sum(p.amount_paise) FILTER (WHERE p.kind = 'charge'), 0)::bigint,
                   coalesce(sum(p.amount_paise) FILTER (WHERE p.kind = 'refund'), 0)::bigint
            FROM payments p JOIN reservations r ON r.id = p.reservation_id
            WHERE r.show_id = @id
            """, new { id = show.Id }, tx);
        return new ShowState(show, seats, charged, refunded);
    }, ct);
}

/// <summary>Show metadata never changes after creation, so it is cached forever once loaded.</summary>
public sealed class ShowCatalog(ShowStore store)
{
    private readonly ConcurrentDictionary<Guid, ShowInfo> _shows = new();

    public void Add(ShowInfo show) => _shows[show.Id] = show;

    public async Task<ShowInfo?> GetAsync(Guid id, CancellationToken ct)
    {
        if (_shows.TryGetValue(id, out var show)) return show;
        show = await store.LoadAsync(id, ct);
        if (show is not null) _shows[id] = show;
        return show;
    }
}
