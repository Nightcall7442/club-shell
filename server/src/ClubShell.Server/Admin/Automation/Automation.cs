using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Users;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using ClubShell.Server.Wallet;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The owner's "if → then" rules (<c>AdminAutomationRule</c>, DESIGN §4.4, §8). Triggers: <c>sessionStarted</c> and
/// <c>visitCount</c> after a session opens (kiosk or counter, not the offline replay), <c>topupAtLeast</c> after a counter
/// top-up — both after the commit of what caused them (§5.3), so a failing rule never undoes it; <c>minutesLeft</c> from the
/// <see cref="ClubTickWorker"/>, <c>pcIdleMinutes</c> from the <see cref="Agents.PcStatusWorker"/>. Every firing is its own
/// transaction and starts with <c>INSERT rule_firings … ON CONFLICT DO NOTHING</c> on (rule, target): a rule fires once per
/// session / top-up / idle stretch, across restarts, and a <c>bonus</c> is credited once (§4.4). Actions: <c>message</c> —
/// command <c>message</c> from the club, no ack; <c>bonus</c> — ledger <c>bonus</c> (never to a transient guest, D-36: the rule
/// still counts as fired); <c>lockPc</c> — <c>lock
/// {reason: staff}</c>; <c>shutdownPc</c> — <c>shutdown {delaySec: 30}</c>; <c>notifyOwner</c> — only the event. Each firing
/// counts <c>fired</c>/<c>lastFiredAt</c> and raises <c>ruleFired</c> for the webhooks. Commands and the wallet push go out
/// after the firing commits.
/// </summary>
public sealed class AutomationService(NpgsqlDataSource db, CommandDispatcher commands, Pushes pushes, TimeProvider clock, ILogger<AutomationService> logger)
{
    /// <summary>
    /// After a session opened (§5.3): <c>sessionStarted</c>, and <c>visitCount</c> when this is the n-th visit of the client
    /// here; the birthday gift (D-107) when it is due.
    /// </summary>
    public async Task SessionOpenedAsync(Guid clubId, Guid sessionId, Guid pcId, Guid userId)
    {
        await BirthdayAsync(clubId, userId, pcId);
        List<Rule> rules;
        long earlier;
        await using (var c = await db.OpenConnectionAsync())
        {
            rules = [.. await RulesAsync(c, clubId, "sessionStarted", "visitCount")];
            if (rules.Count == 0)
            {
                return;
            }

            // Visits before this one; this visit is earlier + 1 (the mock counts the new session inside its own count, §4.4).
            earlier = await c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM sessions WHERE club_id = @clubId AND user_id = @userId AND id <> @sessionId", new { clubId, userId, sessionId });
        }

        foreach (var rule in rules)
        {
            if (rule.Trigger == "sessionStarted" || (rule.Trigger == "visitCount" && rule.Value > 0 && (earlier + 1) % rule.Value == 0))
            {
                await FireAsync(clubId, rule, pcId, userId, $"session:{sessionId}");
            }
        }
    }

    /// <summary>
    /// After a counter top-up of <paramref name="amount"/> (the <c>topUp</c> ledger row <paramref name="entryId"/>).
    /// <paramref name="reachedBefore"/>: the top-up this one corrects (D-91) already fired the rules up to its amount.
    /// </summary>
    public async Task TopUpAsync(Guid clubId, Guid userId, long amount, Guid entryId, long reachedBefore = 0)
    {
        List<Rule> rules;
        await using (var c = await db.OpenConnectionAsync())
        {
            rules = [.. await RulesAsync(c, clubId, "topupAtLeast")];
        }

        foreach (var rule in rules.Where(r => amount >= r.Value && reachedBefore < r.Value))
        {
            await FireAsync(clubId, rule, null, userId, $"topup:{entryId}");
        }
    }

    /// <summary><c>minutesLeft</c>: an active prepaid session with at most <c>value</c> minutes left, once per session.</summary>
    public async Task<int> MinutesLeftAsync(Guid clubId)
    {
        var now = clock.GetUtcNow();
        var fired = 0;
        List<Rule> rules;
        await using (var c = await db.OpenConnectionAsync())
        {
            rules = [.. await RulesAsync(c, clubId, "minutesLeft")];
        }

        foreach (var rule in rules)
        {
            List<(Guid Id, Guid PcId, Guid UserId)> sessions;
            await using (var c = await db.OpenConnectionAsync())
            {
                sessions = (await c.QueryAsync<(Guid, Guid, Guid)>(
                    """
                    SELECT id, pc_id, user_id FROM sessions
                    WHERE club_id = @clubId AND state = 'active' AND is_prepaid AND ends_at >= @now AND ends_at <= @limit
                    ORDER BY ends_at, id
                    """,
                    new { clubId, now, limit = now + TimeSpan.FromMinutes(rule.Value) })).ToList();
            }

            foreach (var s in sessions)
            {
                fired += await FireAsync(clubId, rule, s.PcId, s.UserId, $"session:{s.Id}") ? 1 : 0;
            }
        }

        return fired;
    }

    /// <summary><c>pcIdleMinutes</c>: PCs free since <c>FreeSince</c> for at least <c>value</c> minutes, once per idle stretch.</summary>
    public async Task<int> IdleAsync(IReadOnlyCollection<(Guid ClubId, Guid PcId, DateTimeOffset FreeSince)> free)
    {
        if (free.Count == 0)
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        var fired = 0;
        foreach (var club in free.GroupBy(f => f.ClubId))
        {
            List<Rule> rules;
            await using (var c = await db.OpenConnectionAsync())
            {
                rules = [.. await RulesAsync(c, club.Key, "pcIdleMinutes")];
            }

            foreach (var rule in rules)
            {
                foreach (var (clubId, pcId, since) in club.Where(f => now - f.FreeSince >= TimeSpan.FromMinutes(rule.Value)))
                {
                    fired += await FireAsync(clubId, rule, pcId, null, $"idle:{pcId}:{since.ToUnixTimeMilliseconds()}") ? 1 : 0;
                }
            }
        }

        return fired;
    }

    /// <summary>
    /// The club minute's birthday pass (D-107): every player of an open session in the club whose birthday is due gets the
    /// gift — a session already running at midnight, or opened before the owner turned the gift on.
    /// </summary>
    public async Task<int> BirthdaysAsync(Guid clubId)
    {
        List<(Guid UserId, Guid PcId)> open;
        await using (var c = await db.OpenConnectionAsync())
        {
            var gift = await BirthdayGift.OfClubAsync(c, null, clubId);
            if (!gift.Enabled)
            {
                return 0;
            }

            open = (await c.QueryAsync<(Guid, Guid)>(
                """
                SELECT s.user_id, s.pc_id FROM sessions s JOIN users u ON u.id = s.user_id
                WHERE s.club_id = @clubId AND s.state <> 'ended' AND u.birth_date IS NOT NULL AND NOT u.transient AND u.role IN ('member', 'vip')
                ORDER BY s.started_at, s.id
                """,
                new { clubId })).ToList();
        }

        var fired = 0;
        foreach (var (userId, pcId) in open)
        {
            fired += await BirthdayAsync(clubId, userId, pcId) ? 1 : 0;
        }

        return fired;
    }

    /// <summary>
    /// The birthday gift of <paramref name="userId"/> playing on <paramref name="pcId"/>, when the club's gift is on and the
    /// birthday (club-local) is within its window: in one transaction under the player's lock, <c>rule_firings</c>
    /// (<see cref="BirthdayGift.RuleId"/>, target <c>birthday:{userId}:{year}</c>) — unless any club of the network already
    /// gave the gift of that birthday year, so it is never given twice in a year, across restarts and clubs (the wallet is
    /// the network's) — then the <c>bonus</c> ledger row on the bonus account (D-106) and the message to the PC. A member or
    /// VIP only: never a guest (D-36) nor a staff account. False when nothing was given.
    /// </summary>
    public async Task<bool> BirthdayAsync(Guid clubId, Guid userId, Guid pcId)
    {
        var now = clock.GetUtcNow();
        ServerCommandEnvelope? queued = null;
        var credited = false;
        try
        {
            await using var c = await db.OpenConnectionAsync();
            var gift = await BirthdayGift.OfClubAsync(c, null, clubId);
            if (!gift.Enabled)
            {
                return false;
            }

            await using var tx = await c.BeginTransactionAsync();
            await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
            await AdvisoryLocks.UserAsync(c, tx, userId);
            var who = await c.QuerySingleOrDefaultAsync<(string? BirthDate, string Role, bool Transient, string DisplayName, Guid NetworkId, string TimeZone, string ClubName)?>(
                """
                SELECT to_char(u.birth_date, 'YYYY-MM-DD'), u.role, u.transient, u.display_name, cl.network_id, cl.time_zone,
                       coalesce(nullif(cl.settings -> 'branding' ->> 'clubName', ''), cl.name)
                FROM users u JOIN clubs cl ON cl.id = @clubId AND cl.network_id = u.network_id
                WHERE u.id = @userId AND u.deleted_at IS NULL
                """,
                new { clubId, userId }, tx);
            if (who is not { } w || w.Transient || w.Role is not ("member" or "vip") || Users.BirthDates.Parse(w.BirthDate) is not { } birth
                || Users.BirthDates.BirthdayYear(birth, Users.BirthDates.Today(now, w.TimeZone), gift.WindowDays) is not { } year)
            {
                return false;
            }

            var target = $"birthday:{userId}:{year}";
            if (await c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM rule_firings f JOIN clubs cl ON cl.id = f.club_id WHERE cl.network_id = @NetworkId AND f.target_key = @target)",
                    new { w.NetworkId, target }, tx))
            {
                return false;
            }

            await c.ExecuteAsync(
                "INSERT INTO rule_firings (club_id, rule_id, target_key, fired_at) VALUES (@clubId, @rule, @target, @now)",
                new { clubId, rule = BirthdayGift.RuleId, target, now }, tx);
            if (gift.Amount > 0)
            {
                await Ledger.PostAsync(c, tx, userId, allowOverdraft: false, now, new LedgerLine(
                    "bonus", gift.Amount, "Подарок на день рождения", clubId, PcId: pcId, Meta: new { kind = BirthdayGift.RuleId, year }));
                credited = true;
            }

            queued = await commands.QueueAsync(tx, clubId, pcId, NewCommand.Message(new MessageCommand(
                Guid.NewGuid(), w.ClubName, gift.MessageFor(gift.Amount), NotificationLevel.Info, RequiresAck: false)));
            await Webhooks.EnqueueAsync(c, tx, clubId, "ruleFired", now, $"День рождения — {w.DisplayName}", new { ruleId = BirthdayGift.RuleId, pcId, userId });
            await tx.CommitAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or ApiException)
        {
            logger.LogWarning(ex, "The birthday gift of club {ClubId} failed for {UserId}", clubId, userId);
            return false;
        }

        try
        {
            await commands.SendAsync(pcId, queued!);
            if (credited)
            {
                await pushes.WalletAsync(userId);
            }
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning(ex, "Effects of the birthday gift failed after the commit");
        }

        return true;
    }

    private static async Task<IEnumerable<Rule>> RulesAsync(NpgsqlConnection c, Guid clubId, params string[] kinds) =>
        (await c.QueryAsync<(string Id, string Name, string Trigger, string Action)>(
            "SELECT id, name, trigger::text, action::text FROM automation_rules WHERE club_id = @clubId AND enabled AND trigger ->> 'kind' = ANY(@kinds) ORDER BY position",
            new { clubId, kinds }))
        .Select(r => Rule.Parse(r.Id, r.Name, r.Trigger, r.Action));

    /// <summary>One firing in its own transaction; false when (rule, target) fired before or the firing failed.</summary>
    private async Task<bool> FireAsync(Guid clubId, Rule rule, Guid? pcId, Guid? userId, string target)
    {
        var now = clock.GetUtcNow();
        NewCommand? command = null;
        ServerCommandEnvelope? queued = null;
        var credited = false;
        try
        {
            await using var c = await db.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
            if (await c.ExecuteAsync(
                    "INSERT INTO rule_firings (club_id, rule_id, target_key, fired_at) VALUES (@clubId, @Id, @target, @now) ON CONFLICT DO NOTHING",
                    new { clubId, rule.Id, target, now }, tx) == 0)
            {
                return false;
            }

            var (pcName, userName, transient, clubName) = await c.QuerySingleAsync<(string?, string?, bool?, string)>(
                """
                SELECT (SELECT name FROM pcs WHERE id = @pcId AND club_id = @clubId), (SELECT display_name FROM users WHERE id = @userId),
                       (SELECT transient FROM users WHERE id = @userId), coalesce(nullif(settings -> 'branding' ->> 'clubName', ''), name)
                FROM clubs WHERE id = @clubId
                """,
                new { clubId, pcId, userId }, tx);
            switch (rule.ActionKind)
            {
                case "message" when pcName is not null:
                    command = NewCommand.Message(new MessageCommand(Guid.NewGuid(), clubName, rule.Text, NotificationLevel.Info, RequiresAck: false));
                    break;
                case "lockPc" when pcName is not null:
                    command = NewCommand.Lock(new LockCommand("staff", rule.Name));
                    break;
                case "shutdownPc" when pcName is not null:
                    command = NewCommand.Shutdown(new PowerCommand(30, false, rule.Name));
                    break;
                // Guests never get bonus money (D-36): a throwaway account would carry it off as cash or play time.
                case "bonus" when userName is not null && transient != true && rule.Amount > 0:
                    await Ledger.PostAsync(c, tx, userId!.Value, allowOverdraft: false, now, new LedgerLine("bonus", rule.Amount, $"Бонус: {rule.Name}", clubId));
                    credited = true;
                    break;
            }

            if (command is not null)
            {
                queued = await commands.QueueAsync(tx, clubId, pcId!.Value, command);
            }

            await c.ExecuteAsync(
                "UPDATE automation_rules SET fired = fired + 1, last_fired_at = @now WHERE club_id = @clubId AND id = @Id", new { clubId, rule.Id, now }, tx);
            var who = string.Join(" · ", new[] { pcName, userName }.Where(s => !string.IsNullOrEmpty(s)));
            await Webhooks.EnqueueAsync(c, tx, clubId, "ruleFired", now,
                $"{rule.Name}{(who.Length > 0 ? $" — {who}" : "")}{(rule.ActionKind == "notifyOwner" ? $"\n{rule.Text}" : "")}",
                new { ruleId = rule.Id, pcId, userId });
            await tx.CommitAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or ApiException)
        {
            logger.LogWarning(ex, "Automation rule {RuleId} of club {ClubId} failed for {Target}", rule.Id, clubId, target);
            return false;
        }

        try
        {
            if (queued is not null)
            {
                await commands.SendAsync(pcId!.Value, queued);
            }

            if (credited)
            {
                await pushes.WalletAsync(userId!.Value);
            }
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning(ex, "Effects of automation rule {RuleId} failed after the commit", rule.Id);
        }

        return true;
    }

    private sealed record Rule(string Id, string Name, string Trigger, long Value, string ActionKind, string Text, long Amount)
    {
        public static Rule Parse(string id, string name, string trigger, string action)
        {
            using var t = JsonDocument.Parse(trigger);
            using var a = JsonDocument.Parse(action);
            return new Rule(
                id, name,
                Str(t.RootElement, "kind"), Num(t.RootElement, "value"),
                Str(a.RootElement, "kind"), Str(a.RootElement, "text"), Num(a.RootElement, "amount"));

            static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

            static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        }
    }
}
