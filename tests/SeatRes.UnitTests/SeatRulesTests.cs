using SeatRes.Api.Domain;

namespace SeatRes.UnitTests;

public class SeatRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    [Fact]
    public void Available_is_takeable() =>
        Assert.True(SeatRules.IsTakeable(SeatStatus.Available, null, null, Now));

    [Fact]
    public void Held_is_protected_until_expiry() =>
        Assert.False(SeatRules.IsTakeable(SeatStatus.Held, Now.AddTicks(1), null, Now));

    [Fact]
    public void Held_becomes_takeable_exactly_at_expiry() =>
        Assert.True(SeatRules.IsTakeable(SeatStatus.Held, Now, null, Now));

    [Fact]
    public void Payment_pending_is_protected_until_its_deadline_not_the_hold()
    {
        Assert.False(SeatRules.IsTakeable(SeatStatus.PaymentPending, Now.AddMinutes(-1), Now.AddSeconds(1), Now));
        Assert.True(SeatRules.IsTakeable(SeatStatus.PaymentPending, Now.AddMinutes(-1), Now, Now));
    }

    [Fact]
    public void Confirmed_is_never_takeable() =>
        Assert.False(SeatRules.IsTakeable(SeatStatus.Confirmed, Now.AddYears(-1), Now.AddYears(-1), Now));

    [Theory]
    [InlineData(SeatStatus.Available, 0, 0, "available")]
    [InlineData(SeatStatus.Held, 10, 0, "held")]
    [InlineData(SeatStatus.Held, -10, 0, "available")]
    [InlineData(SeatStatus.PaymentPending, -10, 10, "held")]
    [InlineData(SeatStatus.PaymentPending, -10, -1, "available")]
    [InlineData(SeatStatus.Confirmed, 0, 0, "confirmed")]
    public void Effective_status_maps_storage_to_the_three_reported_states(
        string stored, int holdOffsetSec, int payOffsetSec, string expected)
    {
        var effective = SeatRules.Effective(stored, Now.AddSeconds(holdOffsetSec), Now.AddSeconds(payOffsetSec), Now);
        Assert.Equal(expected, effective);
    }

    [Fact]
    public void Paying_inside_the_hold_pins_until_hold_expiry_plus_grace()
    {
        var hold = Now.AddMinutes(4);
        Assert.Equal(hold + Grace, SeatRules.PayDeadline("held", hold, null, Now, Grace));
    }

    [Fact]
    public void Paying_after_an_unclaimed_hold_expired_pins_for_grace_from_now() =>
        Assert.Equal(Now + Grace, SeatRules.PayDeadline("held", Now.AddMinutes(-2), null, Now, Grace));

    [Fact]
    public void A_running_payment_deadline_is_never_extended()
    {
        var running = Now.AddSeconds(20);
        Assert.Equal(running, SeatRules.PayDeadline("payment_pending", Now.AddMinutes(-1), running, Now, Grace));
    }

    [Fact]
    public void An_expired_payment_deadline_restarts_from_now() =>
        Assert.Equal(Now + Grace, SeatRules.PayDeadline("payment_pending", Now.AddMinutes(-3), Now.AddSeconds(-1), Now, Grace));

    [Fact]
    public void Hold_and_pay_in_one_call_pins_for_grace_from_now() =>
        Assert.Equal(Now + Grace, SeatRules.PayDeadline("new", null, null, Now, Grace));

    [Theory]
    [InlineData("A1", true)]
    [InlineData("AA-12", true)]
    [InlineData("", false)]
    [InlineData("A 1", false)]
    [InlineData("A1;DROP", false)]
    [InlineData("ABCDEFGHIJKLMNOPQ", false)]
    public void Seat_labels_are_short_and_safe(string label, bool valid) =>
        Assert.Equal(valid, SeatLabel.IsValid(label));
}

public class RequestHashTests
{
    private static readonly Guid Show = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    [Fact]
    public void Seat_order_does_not_change_the_hash() =>
        Assert.Equal(RequestHash.ForReserve(Show, ["A2", "A1"], false), RequestHash.ForReserve(Show, ["A1", "A2"], false));

    [Fact]
    public void Different_seats_change_the_hash() =>
        Assert.NotEqual(RequestHash.ForReserve(Show, ["A1"], false), RequestHash.ForReserve(Show, ["A2"], false));

    [Fact]
    public void Confirm_flag_changes_the_hash() =>
        Assert.NotEqual(RequestHash.ForReserve(Show, ["A1"], false), RequestHash.ForReserve(Show, ["A1"], true));

    [Fact]
    public void Different_show_changes_the_hash() =>
        Assert.NotEqual(RequestHash.ForReserve(Show, ["A1"], false), RequestHash.ForReserve(Guid.NewGuid(), ["A1"], false));

    [Fact]
    public void Labels_are_case_sensitive() =>
        Assert.NotEqual(RequestHash.ForReserve(Show, ["a1"], false), RequestHash.ForReserve(Show, ["A1"], false));

    [Fact]
    public void Confirm_hash_is_bound_to_the_reservation() =>
        Assert.NotEqual(RequestHash.ForConfirm(Guid.NewGuid()), RequestHash.ForConfirm(Guid.NewGuid()));
}

public class IdempotencyKeyTests
{
    [Fact]
    public void Header_key_is_used() => Assert.Equal("k1", IdempotencyKey.Resolve("k1", null).Key);

    [Fact]
    public void Body_key_is_used() => Assert.Equal("k1", IdempotencyKey.Resolve(null, "k1").Key);

    [Fact]
    public void Same_key_in_both_places_is_fine() => Assert.Equal("k1", IdempotencyKey.Resolve("k1", "k1").Key);

    [Fact]
    public void Conflicting_header_and_body_keys_are_rejected()
    {
        var (key, error) = IdempotencyKey.Resolve("k1", "k2");
        Assert.Null(key);
        Assert.Equal(400, error!.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_key_is_rejected(string? key) => Assert.Equal(400, IdempotencyKey.Resolve(key, null).Error!.StatusCode);

    [Fact]
    public void Overlong_key_is_rejected() =>
        Assert.Equal(400, IdempotencyKey.Resolve(new string('k', 129), null).Error!.StatusCode);

    [Fact]
    public void Control_characters_are_rejected() =>
        Assert.Equal(400, IdempotencyKey.Resolve("ab\ncd", null).Error!.StatusCode);
}
