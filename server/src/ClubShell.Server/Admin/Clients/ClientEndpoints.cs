using System.Security.Cryptography;
using System.Text.Json;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Sessions;
using ClubShell.Server.Sessions.Billing;
using ClubShell.Server.Wallet;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Clients at the counter (slice S5): search, registration, the card, the club profile, a new password and the wallet
/// history; beyond the contract, the counter's picker (<c>GET /admin/clients/lookup</c>, cashier and owner). A client is a
/// user of the staff member's network that is neither deleted nor a guest; the list also leaves out staff accounts
/// (<c>role = admin</c>). Users and wallets are network-level (DESIGN §4.1), the profile (group, note, phone, birth year,
/// blacklist) belongs to the club. Login name and card are unique case-insensitively (<c>users_username</c>,
/// <c>users_card</c>); the name is stored lower-case. Passwords are PBKDF2 (<see cref="Passwords"/>), never logged or
/// journaled. A new password revokes every player token of the client, blacklisting those on the club's PCs; both push
/// <c>userRevoked</c> after the commit (D-21, §6.5). Only the owner changes <c>blacklisted</c> (<c>403 ownerOnly</c>,
/// before any field check). Group and blacklist changes, the card and the password go to the journal (§3.7).
/// </summary>
public static class ClientEndpoints
{
    public static readonly string[] Operations =
    [
        "adminClients", "adminAddClient", "adminUpdateClient", "adminBindClientCard", "adminSetClientPassword", "adminClientTransactions",
    ];

    public static void MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/clients").WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapGet("", ListAsync);
        api.MapGet("/lookup", LookupAsync);
        api.MapPost("", AddAsync);
        api.MapPatch("/{id}", UpdateAsync);
        api.MapPost("/{id}/card", CardAsync);
        api.MapPost("/{id}/password", PasswordAsync);
        api.MapGet("/{id}/transactions", TransactionsAsync);
    }

    /// <summary>
    /// <c>q</c>: a case-insensitive substring of the name or login, or a substring of the phone; empty — everyone. Matched
    /// here, not by SQL <c>lower()</c>, which folds only ASCII under a <c>C</c> database locale (Cyrillic names).
    /// ponytail: the whole client list is read per search, as the contract has no paging; add a trigram index when a club
    /// outgrows it.
    /// </summary>
    private static async Task<IResult> ListAsync(HttpContext context, string? q, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var term = (q ?? "").Trim().ToLowerInvariant();
        await using var c = await db.OpenConnectionAsync();
        var club = await SessionService.ClubAsync(c, null, staff.ClubId);
        var rows = await c.QueryAsync<ClientRow>(
            $"""
            {ClientRow.Select}
              AND u.role NOT IN ('guest', 'admin')
            ORDER BY u.display_name, u.id
            """,
            new { staff.ClubId, staff.NetworkId });
        var items = rows
            .Where(r => term.Length == 0 || r.DisplayName.ToLowerInvariant().Contains(term, StringComparison.Ordinal)
                || r.Username.ToLowerInvariant().Contains(term, StringComparison.Ordinal) || r.Phone.Contains(term, StringComparison.Ordinal))
            .Select(r => r.ToWire(club.Pricing))
            .ToList();
        return AdminJson.Ok(new AdminClientList(items));
    }

    /// <summary>
    /// The counter's client picker (beyond the contract): at most 8 clients of the list's kind, best match first — the card
    /// or the login exactly, then the login or a word of the name starting with <c>q</c>, then <c>q</c> inside either, then
    /// inside the phone's digits ("4521" finds +998 90 123 45 21); within a rank, and for a <c>q</c> under 2 characters,
    /// the most recently active first (a login at a PC, a wallet movement, registration). ponytail: matched in memory, as
    /// <see cref="ListAsync"/> does.
    /// </summary>
    private static async Task<IResult> LookupAsync(HttpContext context, string? q, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var term = (q ?? "").Trim();
        var recent = term.Length < 2;
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<LookupRow>(
            $"""
            {LookupRow.Select}
            ORDER BY active_at DESC, u.id
            {(recent ? "LIMIT 8" : "")}
            """,
            new { staff.ClubId, staff.NetworkId });
        var text = term.ToLowerInvariant();
        var digits = QueryDigits(term);
        var items = recent ? rows : rows.Select(r => (Row: r, Rank: r.Rank(text, digits))).Where(m => m.Rank >= 0).OrderBy(m => m.Rank).Select(m => m.Row);
        return AdminJson.Ok(new AdminClientLookupList(items.Take(8).Select(r => r.ToWire()).ToList()));
    }

    /// <summary>The digits of a <c>q</c> that reads as a phone (digits, spaces, <c>+ - ( )</c>), else null.</summary>
    private static string? QueryDigits(string term) =>
        term.Any(char.IsAsciiDigit) && term.All(ch => char.IsAsciiDigit(ch) || ch is ' ' or '+' or '-' or '(' or ')')
            ? string.Concat(term.Where(char.IsAsciiDigit))
            : null;

    /// <summary>
    /// A member with zero balances and the club profile. No <c>password</c>: a random one nobody knows (only a reset gives
    /// the client one). Checks in the mock's order: fields, login taken, card taken.
    /// </summary>
    private static async Task<IResult> AddAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminClientCreateRequest>(body, "displayName", "username");
        var displayName = AdminInput.Text(r.DisplayName, "displayName", 64);
        var username = AdminInput.Text(r.Username, "username", 32).ToLowerInvariant();
        var password = r.Password is null ? null : Password(r.Password);
        var phone = AdminInput.OptionalText(r.Phone, "phone", 32) ?? "";
        var birthYear = AdminInput.Range(r.BirthYear, "birthYear", 1900, 2100);
        var groupId = AdminInput.OptionalText(r.GroupId, "groupId", 64);
        var card = Card(r.CardId);

        // PBKDF2 outside the transaction: it holds no lock while it derives.
        var hash = Passwords.Hash(password ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: false, body, async (c, tx) =>
        {
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7(now);
            if (await c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM users WHERE network_id = @NetworkId AND lower(username) = @username)", new { staff.NetworkId, username }, tx))
            {
                throw ApiException.Validation("username", "taken");
            }

            await CardFreeAsync(c, tx, staff, card, id);
            await UniqueAsync(c, tx, () => c.ExecuteAsync(
                """
                INSERT INTO users (id, network_id, username, display_name, role, password_hash, card_id, created_at)
                VALUES (@id, @NetworkId, @username, @displayName, 'member', @hash, @card, @now);
                INSERT INTO wallets (user_id, network_id, updated_at) VALUES (@id, @NetworkId, @now);
                INSERT INTO client_profiles (club_id, user_id, group_id, phone, birth_year, updated_at)
                VALUES (@ClubId, @id, @groupId, @phone, @birthYear, @now);
                """,
                new { id, staff.NetworkId, staff.ClubId, username, displayName, hash, card, groupId, phone, birthYear, now },
                tx));
            await Audit.WriteAsync(c, tx, staff, now, "clientAdd", id, detail: $"{displayName} · {username}");
            if (groupId is not null)
            {
                // Like a PATCH into a group: the control page flags a big discount from this entry.
                await GroupAuditAsync(c, tx, staff, now, id, groupId, displayName);
            }

            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminClientResponse(await ViewAsync(c, tx, staff, id))));
        });
    }

    /// <summary>
    /// Partial: a key that is present is applied (<c>null</c> clears <c>groupId</c>, <c>note</c>, <c>phone</c>,
    /// <c>birthYear</c>), an absent one is left; <c>telegram</c> and unknown keys are ignored.
    /// </summary>
    private static async Task<IResult> UpdateAsync(
        HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, Pushes pushes, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (AdminInput.Has(body, "blacklisted") && !staff.IsOwner)
        {
            throw ApiException.Forbidden("ownerOnly", "Only the owner changes the blacklist");
        }

        var r = Api.Read<AdminClientUpdateRequest>(body);

        // Whatever spelling the binder took for the key, a value it bound is the owner's alone.
        if (r.Blacklisted is not null && !staff.IsOwner)
        {
            throw ApiException.Forbidden("ownerOnly", "Only the owner changes the blacklist");
        }

        var displayName = r.DisplayName is null ? null : AdminInput.Text(r.DisplayName, "displayName", 64);
        var groupId = AdminInput.OptionalText(r.GroupId, "groupId", 64);
        var note = AdminInput.OptionalText(r.Note, "note", 2000);
        var phone = AdminInput.OptionalText(r.Phone, "phone", 32);
        var birthYear = AdminInput.Range(r.BirthYear, "birthYear", 1900, 2100);
        if (AdminInput.Has(body, "blacklisted") && r.Blacklisted is null)
        {
            throw ApiException.Validation("blacklisted", "format");
        }

        var userId = ClientId(id);
        IReadOnlyList<Guid> revoked = [];
        AdminClient client;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var name = await LockAsync(c, tx, staff, userId);
            await c.ExecuteAsync(
                "INSERT INTO client_profiles (club_id, user_id, updated_at) VALUES (@ClubId, @userId, @now) ON CONFLICT DO NOTHING",
                new { staff.ClubId, userId, now }, tx);
            var before = await c.QuerySingleAsync<(string? GroupId, bool Blacklisted)>(
                "SELECT group_id, blacklisted FROM client_profiles WHERE club_id = @ClubId AND user_id = @userId FOR UPDATE", new { staff.ClubId, userId }, tx);
            await c.ExecuteAsync(
                """
                UPDATE client_profiles
                SET group_id = CASE WHEN @hasGroup THEN @groupId ELSE group_id END,
                    note = CASE WHEN @hasNote THEN coalesce(@note, '') ELSE note END,
                    phone = CASE WHEN @hasPhone THEN coalesce(@phone, '') ELSE phone END,
                    birth_year = CASE WHEN @hasBirthYear THEN @birthYear ELSE birth_year END,
                    blacklisted = coalesce(@Blacklisted, blacklisted), updated_at = @now
                WHERE club_id = @ClubId AND user_id = @userId;
                UPDATE users SET display_name = coalesce(@displayName, display_name) WHERE id = @userId;
                """,
                new
                {
                    staff.ClubId, userId, now, displayName, groupId, note, phone, birthYear, r.Blacklisted,
                    hasGroup = AdminInput.Has(body, "groupId"), hasNote = AdminInput.Has(body, "note"), hasPhone = AdminInput.Has(body, "phone"),
                    hasBirthYear = AdminInput.Has(body, "birthYear"),
                },
                tx);
            name = displayName ?? name;
            var group = AdminInput.Has(body, "groupId") ? groupId : before.GroupId;
            if (group != before.GroupId)
            {
                await GroupAuditAsync(c, tx, staff, now, userId, group, name);
            }

            if (r.Blacklisted is { } blacklisted && blacklisted != before.Blacklisted)
            {
                await Audit.WriteAsync(c, tx, staff, now, "blacklist", userId, detail: name, meta: new { blacklisted });
                if (blacklisted)
                {
                    revoked = (await c.QueryAsync<Guid>(
                        "DELETE FROM user_tokens t USING pcs p WHERE t.pc_id = p.id AND p.club_id = @ClubId AND t.user_id = @userId RETURNING t.pc_id",
                        new { staff.ClubId, userId }, tx)).ToList();
                }
            }

            client = await ViewAsync(c, tx, staff, userId);
            await tx.CommitAsync();
        }

        await pushes.UserRevokedAsync(userId, "blacklisted", revoked);
        return AdminJson.Ok(new AdminClientResponse(client));
    }

    /// <summary><c>cardId</c> null or empty unbinds; a card another client holds (any case) is <c>400 cardId taken</c>.</summary>
    private static async Task<IResult> CardAsync(HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var r = Api.Read<AdminClientCardRequest>(body);
        if (!AdminInput.Has(body, "cardId"))
        {
            throw ApiException.Validation("cardId", "required");
        }

        var card = Card(r.CardId);
        var userId = ClientId(id);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var now = clock.GetUtcNow();
        var name = await LockAsync(c, tx, staff, userId);
        await CardFreeAsync(c, tx, staff, card, userId);
        await UniqueAsync(c, tx, () => c.ExecuteAsync("UPDATE users SET card_id = @card WHERE id = @userId", new { card, userId }, tx));
        await Audit.WriteAsync(c, tx, staff, now, "clientCard", userId, detail: $"{name} · {card ?? "—"}", meta: new { cardId = card });
        var client = await ViewAsync(c, tx, staff, userId);
        await tx.CommitAsync();
        return AdminJson.Ok(new AdminClientResponse(client));
    }

    /// <summary>
    /// The new password replaces the old one at once: every player token of the client is revoked (the PCs get
    /// <c>userRevoked</c>) and the failed-login counter of the name is cleared.
    /// </summary>
    private static async Task<IResult> PasswordAsync(
        HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, Pushes pushes, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var hash = Passwords.Hash(Password(Api.Read<AdminClientPasswordRequest>(body, "password").Password!));
        var userId = ClientId(id);
        IReadOnlyList<Guid> revoked;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var name = await LockAsync(c, tx, staff, userId);
            await c.ExecuteAsync(
                """
                UPDATE users SET password_hash = @hash WHERE id = @userId;
                DELETE FROM login_failures WHERE network_id = @NetworkId AND username = (SELECT lower(username) FROM users WHERE id = @userId);
                """,
                new { hash, userId, staff.NetworkId }, tx);
            revoked = (await c.QueryAsync<Guid>("DELETE FROM user_tokens WHERE user_id = @userId RETURNING pc_id", new { userId }, tx)).ToList();
            await Audit.WriteAsync(c, tx, staff, now, "clientPassword", userId, detail: name);
            await tx.CommitAsync();
        }

        await pushes.UserRevokedAsync(userId, "passwordReset", revoked);
        return AdminJson.Ok(AdminJson.OkBody);
    }

    /// <summary>
    /// Up to 100 newest wallet rows of the client (network-level, like the wallet). An unknown client is an empty list, as
    /// the contract says (no 404 is declared).
    /// </summary>
    private static async Task<IResult> TransactionsAsync(HttpContext context, string id, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (!Guid.TryParse(id, out var userId))
        {
            return AdminJson.Ok(new AdminTransactionList([]));
        }

        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<TransactionRow>(
            $"""
            SELECT {TransactionRow.Columns}
            FROM ledger_entries WHERE user_id = @userId AND network_id = @NetworkId
            ORDER BY created_at DESC, id DESC LIMIT 100
            """,
            new { userId, staff.NetworkId });
        return AdminJson.Ok(new AdminTransactionList(rows.Select(t => t.ToWire()).ToList()));
    }

    /// <summary>A client moved into <paramref name="group"/> (null — out of any): the <c>clientGroup</c> entry with the group's discount.</summary>
    private static async Task GroupAuditAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, DateTimeOffset now, Guid userId, string? group, string name)
    {
        var pricing = (await SessionService.ClubAsync(c, tx, staff.ClubId)).Pricing;
        var g = pricing.Groups.FirstOrDefault(x => x.Id == group);
        await Audit.WriteAsync(c, tx, staff, now, "clientGroup", userId, detail: $"{name} → {g?.Name ?? "—"}",
            meta: new { groupId = group, groupName = g?.Name, discountPct = g?.DiscountPct ?? 0, clientName = name });
    }

    /// <summary>The client as the console draws it; the caller made sure it exists.</summary>
    public static async Task<AdminClient> ViewAsync(NpgsqlConnection c, NpgsqlTransaction? tx, StaffContext staff, Guid id)
    {
        var club = await SessionService.ClubAsync(c, tx, staff.ClubId);
        var row = await c.QuerySingleAsync<ClientRow>($"{ClientRow.Select} AND u.id = @id", new { staff.ClubId, staff.NetworkId, id }, tx);
        return row.ToWire(club.Pricing);
    }

    /// <summary>
    /// A client of the network (not deleted, not a guest, not a staff account — the list hides <c>role = admin</c>, so the
    /// writes must not reach it either), locked; its display name, else <c>404 what=user</c>.
    /// </summary>
    public static async Task<string> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, Guid userId) =>
        await c.QuerySingleOrDefaultAsync<string>(
            "SELECT display_name FROM users WHERE id = @userId AND network_id = @NetworkId AND deleted_at IS NULL AND NOT transient AND role <> 'admin' FOR UPDATE",
            new { userId, staff.NetworkId }, tx)
        ?? throw ApiException.NotFound("user");

    private static Guid ClientId(string id) => Guid.TryParse(id, out var userId) ? userId : throw ApiException.NotFound("user");

    /// <summary>4–64 characters: empty — <c>required</c>, shorter — <c>min</c>, longer — <c>max</c> (the mock's <c>passwordOf</c>).</summary>
    private static string Password(string password) =>
        AdminInput.Text(password, "password", 64).Length < 4 ? throw ApiException.Validation("password", "min") : password;

    /// <summary>Trimmed; empty or null — no card; longer than 64 — <c>max</c>.</summary>
    private static string? Card(string? card) =>
        AdminInput.OptionalText(string.IsNullOrWhiteSpace(card) ? null : card.Trim(), "cardId", 64);

    private static async Task CardFreeAsync(NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, string? card, Guid userId)
    {
        if (card is not null && await c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM users WHERE network_id = @NetworkId AND lower(card_id) = lower(@card) AND id <> @userId)",
                new { staff.NetworkId, card, userId }, tx))
        {
            throw ApiException.Validation("cardId", "taken");
        }
    }

    /// <summary>A login or card that a concurrent request took after the check: <c>400 taken</c> all the same.</summary>
    private static async Task UniqueAsync(NpgsqlConnection c, NpgsqlTransaction tx, Func<Task<int>> write)
    {
        await c.ExecuteAsync("SAVEPOINT client_unique", transaction: tx);
        try
        {
            await write();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName is "users_username" or "users_card")
        {
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT client_unique", transaction: tx);
            throw ApiException.Validation(ex.ConstraintName == "users_username" ? "username" : "cardId", "taken");
        }
    }

    private sealed class ClientRow
    {
        /// <summary>Clients of the network with this club's profile; <c>visits</c> = sessions in this club.</summary>
        public const string Select =
            """
            SELECT u.id, u.username, u.display_name, u.role, u.card_id, w.main_balance, w.lifetime_spent,
                   cp.group_id, coalesce(cp.note, '') AS note, coalesce(cp.blacklisted, false) AS blacklisted,
                   coalesce(cp.phone, '') AS phone, cp.birth_year,
                   (SELECT count(*) FROM sessions s WHERE s.user_id = u.id AND s.club_id = @ClubId)::int AS visits
            FROM users u
            JOIN wallets w ON w.user_id = u.id
            LEFT JOIN client_profiles cp ON cp.club_id = @ClubId AND cp.user_id = u.id
            WHERE u.network_id = @NetworkId AND u.deleted_at IS NULL AND NOT u.transient
            """;

        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Role { get; init; } = "";
        public string? CardId { get; init; }
        public long MainBalance { get; init; }
        public long LifetimeSpent { get; init; }
        public string? GroupId { get; init; }
        public string Note { get; init; } = "";
        public bool Blacklisted { get; init; }
        public string Phone { get; init; } = "";
        public int? BirthYear { get; init; }
        public int Visits { get; init; }

        /// <summary>The level reached by the lifetime spend; below the first one — the first (as the mock); no levels — 1, unnamed.</summary>
        public AdminClient ToWire(ClubPricing club)
        {
            var level = club.LevelOf(LifetimeSpent) ?? club.Loyalty.FirstOrDefault();
            return new AdminClient(
                Id, Username, DisplayName, Role, Money.Uzs(MainBalance), Money.Uzs(0), GroupId, Note, Blacklisted, Phone, BirthYear, CardId,
                LifetimeSpent, Visits, level?.Level ?? 1, level?.Name ?? "");
        }
    }

    private sealed class LookupRow
    {
        /// <summary>
        /// The list's clients with this club's phone, the PC of the open session in this club (<c>sessions_open_user</c>: one at
        /// most) and the last sign of life.
        /// </summary>
        public const string Select =
            """
            SELECT u.id, u.username, u.display_name, u.card_id, w.main_balance, coalesce(cp.phone, '') AS phone,
                   s.pc_id AS playing_pc_id, p.name AS playing_pc_name, greatest(u.last_seen_at, w.updated_at, u.created_at) AS active_at
            FROM users u
            JOIN wallets w ON w.user_id = u.id
            LEFT JOIN client_profiles cp ON cp.club_id = @ClubId AND cp.user_id = u.id
            LEFT JOIN sessions s ON s.user_id = u.id AND s.club_id = @ClubId AND s.state <> 'ended'
            LEFT JOIN pcs p ON p.id = s.pc_id
            WHERE u.network_id = @NetworkId AND u.deleted_at IS NULL AND NOT u.transient AND u.role NOT IN ('guest', 'admin')
            """;

        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string? CardId { get; init; }
        public long MainBalance { get; init; }
        public string Phone { get; init; } = "";
        public Guid? PlayingPcId { get; init; }
        public string? PlayingPcName { get; init; }

        private string PhoneDigits => string.Concat(Phone.Where(char.IsAsciiDigit));

        /// <summary>
        /// 0 — the card (any case) or the login is <paramref name="text"/>, 1 — the login or a word of the name starts with it,
        /// 2 — it is inside either, 3 — <paramref name="digits"/> are inside the phone's; −1 — no match. <paramref name="text"/>
        /// is lower-cased by the caller, the name and the login here (Cyrillic, see <see cref="ListAsync"/>).
        /// </summary>
        public int Rank(string text, string? digits)
        {
            var login = Username.ToLowerInvariant();
            var name = DisplayName.ToLowerInvariant();
            if (login == text || string.Equals(CardId, text, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (login.StartsWith(text, StringComparison.Ordinal) || name.StartsWith(text, StringComparison.Ordinal) || name.Contains(" " + text, StringComparison.Ordinal))
            {
                return 1;
            }

            if (login.Contains(text, StringComparison.Ordinal) || name.Contains(text, StringComparison.Ordinal))
            {
                return 2;
            }

            return digits is not null && PhoneDigits.Contains(digits, StringComparison.Ordinal) ? 3 : -1;
        }

        public AdminClientLookupItem ToWire()
        {
            var phone = PhoneDigits;
            return new AdminClientLookupItem(
                Id, DisplayName, Username, phone.Length == 0 ? null : phone[^Math.Min(4, phone.Length)..], Money.Uzs(MainBalance), Money.Uzs(0), CardId,
                PlayingPcId is { } pcId ? new AdminClientPlaying(pcId, PlayingPcName ?? "") : null);
        }
    }
}
