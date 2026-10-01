-- Seat reservation schema. One row per seat is the single source of truth for who owns it.

CREATE TABLE shows (
    id              uuid PRIMARY KEY,
    name            text        NOT NULL,
    price_paise     bigint      NOT NULL CHECK (price_paise >= 0),
    per_user_limit  int         NOT NULL DEFAULT 4 CHECK (per_user_limit > 0),
    total_seats     int         NOT NULL CHECK (total_seats > 0),
    created_at      timestamptz NOT NULL
);

CREATE TABLE seats (
    show_id          uuid        NOT NULL REFERENCES shows (id),
    seat_no          int         NOT NULL,              -- creation order; the global lock order
    label            text        NOT NULL,
    status           text        NOT NULL CHECK (status IN ('available', 'held', 'payment_pending', 'confirmed')),
    reservation_id   uuid        NULL,
    holder_user_id   text        NULL,
    hold_expires_at  timestamptz NULL,
    pay_deadline     timestamptz NULL,
    PRIMARY KEY (show_id, seat_no),
    UNIQUE (show_id, label),
    -- A seat is owned iff it is not available; an owner always has a user. Two owners are unrepresentable.
    CHECK ((status = 'available') = (reservation_id IS NULL)),
    CHECK ((reservation_id IS NULL) = (holder_user_id IS NULL))
);
CREATE INDEX seats_holder ON seats (show_id, holder_user_id) WHERE reservation_id IS NOT NULL;
CREATE INDEX seats_reservation ON seats (reservation_id) WHERE reservation_id IS NOT NULL;

CREATE TABLE reservations (
    id               uuid PRIMARY KEY,
    show_id          uuid        NOT NULL REFERENCES shows (id),
    user_id          text        NOT NULL,
    seat_nos         int[]       NOT NULL,
    labels           text[]      NOT NULL,
    amount_paise     bigint      NOT NULL CHECK (amount_paise >= 0),
    status           text        NOT NULL CHECK (status IN ('held', 'payment_pending', 'confirmed', 'cancelled',
                                                           'expired', 'refund_pending', 'refunded')),
    hold_expires_at  timestamptz NULL,
    pay_deadline     timestamptz NULL,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);
CREATE INDEX reservations_show ON reservations (show_id);

-- Lock row only: serialises one user's writes for one show (per-user limit).
CREATE TABLE user_show_locks (
    user_id  text NOT NULL,
    show_id  uuid NOT NULL,
    PRIMARY KEY (user_id, show_id)
);

CREATE TABLE idempotency_keys (
    user_id          text        NOT NULL,
    scope            text        NOT NULL CHECK (scope IN ('reserve', 'confirm')),
    key              text        NOT NULL,
    request_hash     bytea       NOT NULL,
    reservation_id   uuid        NULL,
    response_status  int         NULL,     -- NULL while the request is still in progress
    response_body    jsonb       NULL,
    created_at       timestamptz NOT NULL,
    PRIMARY KEY (user_id, scope, key)
);

CREATE TABLE payment_attempts (
    id              uuid PRIMARY KEY,
    reservation_id  uuid        NOT NULL REFERENCES reservations (id),
    attempt_no      int         NOT NULL,
    gateway_key     text        NOT NULL UNIQUE,
    idem_user       text        NOT NULL,
    idem_scope      text        NOT NULL,
    idem_key        text        NOT NULL,
    amount_paise    bigint      NOT NULL CHECK (amount_paise >= 0),
    status          text        NOT NULL CHECK (status IN ('pending', 'succeeded', 'declined', 'unknown',
                                                          'refund_pending', 'refunded')),
    refund_reason   text        NULL,
    created_at      timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL,
    UNIQUE (reservation_id, attempt_no)
);
-- At most one payment in flight per reservation.
CREATE UNIQUE INDEX payment_attempts_one_inflight ON payment_attempts (reservation_id)
    WHERE status IN ('pending', 'unknown');
CREATE INDEX payment_attempts_recovery ON payment_attempts (status, updated_at)
    WHERE status IN ('pending', 'unknown', 'refund_pending');

-- Our money ledger: every charge and refund the gateway confirmed.
CREATE TABLE payments (
    id              uuid PRIMARY KEY,
    reservation_id  uuid        NOT NULL REFERENCES reservations (id),
    attempt_id      uuid        NULL REFERENCES payment_attempts (id),
    kind            text        NOT NULL CHECK (kind IN ('charge', 'refund')),
    amount_paise    bigint      NOT NULL CHECK (amount_paise > 0),
    gateway_ref     text        NOT NULL,
    reason          text        NULL,
    created_at      timestamptz NOT NULL,
    UNIQUE (gateway_ref, kind)
);
CREATE INDEX payments_reservation ON payments (reservation_id);

-- The simulated payment provider's own book (stands in for an external system).
CREATE TABLE gateway_charges (
    gateway_key   text PRIMARY KEY,
    amount_paise  bigint      NOT NULL,
    status        text        NOT NULL CHECK (status IN ('succeeded', 'declined')),
    refunded      boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL
);
