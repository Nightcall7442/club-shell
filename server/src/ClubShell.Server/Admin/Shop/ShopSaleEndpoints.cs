using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Sessions;
using ClubShell.Server.Sessions.Billing;
using ClubShell.Server.Wallet;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// The bar at the desk (cash desk part 3, D-52..D-57, beyond the contract): <c>POST /admin/shop/sales</c> sells goods at list
/// price, and <c>POST /admin/shop/sales/{id}/void</c> cancels a whole sale. Staff members only (the club API key has no
/// drawer, <c>403 staffOnly</c>), in an open shift, <c>Idempotency-Key</c> required with the strict body check (D-70), and the
/// desk's <c>saleId</c> is the sale's primary key, so one cart is never booked twice, not even under a new key. A sale paid
/// by a method (cash, card, Payme, Click, Uzum) is a <c>shop_sales</c> row and its lines only — no wallet moves, the buyer
/// (a member or a guest) is just named; a sale from the balance also posts a <c>purchase</c> ledger row of −total, never
/// into debt and never past what an open postpaid session will still charge (D-55). Stock goes down by one conditional
/// UPDATE per line in product id order (D-54): a sale never writes <c>in_stock</c>, a refusal (<c>outOfStock</c>,
/// <c>notSellable</c>, <c>priceChanged</c>, <c>insufficientFunds</c>) rolls the whole cart back. A void (D-56) needs a reason; a
/// cashier voids within 15 minutes, the owner later; a method sale only in its own shift, a balance sale in any open shift
/// (a <c>purchase</c> row of +total, never <c>refund</c>); <c>defect</c> does not restock. Lock order (D-69): the sale row →
/// the wallet → products by id → the shift (the strong lock of a cash void last).
/// </summary>
public static class ShopSaleEndpoints
{
    /// <summary>A cashier voids a sale only this long after it; the owner at any time (D-56).</summary>
    public static readonly TimeSpan CashierVoidWindow = TimeSpan.FromMinutes(15);

    private static readonly string[] VoidReasons = ["mistake", "returned", "defect", "other"];

    public static void MapShopSaleEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/shop/sales").WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPost("", SellAsync);
        api.MapPost("/{id}/void", VoidAsync);
    }

    /// <summary>
    /// <c>201 {sale, balance, products, expectedCash}</c>. In the key's transaction: an open shift; the <c>saleId</c> not booked
    /// yet (else <c>409 saleExists {sale}</c>); for a balance sale the wallet locked; every line decremented; the desk's total
    /// equal to the server's (else <c>409 priceChanged {total, prices}</c>); the money; the rows, the journal entry
    /// (<c>shopSale</c>) and the <c>lowStock</c> events. <c>walletUpdated</c> after the commit for a balance sale.
    /// </summary>
    private static async Task<IResult> SellAsync(
        HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, PcRepository pcs)
    {
        var staff = CounterEndpoints.StaffOnly(context);
        var r = Api.Read<AdminShopSaleRequest>(body, "saleId", "items", "total");
        var saleId = r.SaleId!.Value == Guid.Empty ? throw ApiException.Validation("saleId", "format") : r.SaleId.Value;
        var items = Items(r.Items);
        var total = r.Total!.Value < 1 ? throw ApiException.Validation("total", "min")
            : r.Total.Value > 100_000_000 ? throw ApiException.Validation("total", "max") : r.Total.Value;
        var payment = CounterEndpoints.PaymentOf(r.Payment);
        if (payment is { } pay && pay.Amount != total)
        {
            throw ApiException.Validation("payment.amount", "total");
        }

        if (payment is null && r.UserId is null)
        {
            throw ApiException.Validation("userId", "required");
        }

        var pc = r.PcId is { } pcId ? await CounterEndpoints.LivePcAsync(pcs, staff, pcId) : null;
        var method = payment?.Method ?? "balance";
        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM shop_sales WHERE id = @saleId)", new { saleId }, tx))
            {
                throw SaleExists(await SaleAsync(c, tx, staff.ClubId, saleId));
            }

            // The buyer: a balance sale locks the wallet first (§4.4: wallets → products → shifts); a method sale only names them.
            BuyerRow? buyer = null;
            if (r.UserId is { } userId)
            {
                buyer = await c.QuerySingleOrDefaultAsync<BuyerRow>(
                    $"""
                    SELECT u.id, u.display_name, u.role, w.main_balance AS balance FROM users u JOIN wallets w ON w.user_id = u.id
                    WHERE u.id = @userId AND u.network_id = @NetworkId AND u.deleted_at IS NULL
                    {(payment is null ? "FOR UPDATE OF w" : "")}
                    """,
                    new { userId, staff.NetworkId }, tx)
                    ?? throw ApiException.NotFound("user");
            }

            var sold = new Dictionary<Guid, StockEndpoints.ProductRow>();
            foreach (var (productId, qty) in items.OrderBy(i => i.ProductId))
            {
                sold[productId] = await DecrementAsync(c, tx, staff, productId, qty, now);
            }

            var lines = items.Select(i => new AdminSaleLine(i.ProductId, sold[i.ProductId].Title, i.Qty, sold[i.ProductId].Price, i.Qty * sold[i.ProductId].Price)).ToList();
            var serverTotal = lines.Sum(l => l.Amount);
            if (serverTotal != total)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: priceChanged", new
                {
                    reason = "priceChanged", total = Money.Uzs(serverTotal),
                    prices = lines.Select(l => new { productId = l.ProductId, price = Money.Uzs(l.Price) }).ToList(),
                });
            }

            var meta = lines.Select(l => new { productId = l.ProductId, title = l.Title, qty = l.Qty, price = l.Price }).ToList();
            Guid shiftId;
            Guid? ledgerId = null;
            long? balance = null;
            if (payment is null)
            {
                var spendable = await SpendableAsync(c, tx, buyer!.Id, buyer.Balance, now);
                if (spendable < total)
                {
                    throw new ApiException(StatusCodes.Status402PaymentRequired, ErrorCode.InsufficientFunds, "Balance too low",
                        new { required = Money.Uzs(total), available = Money.Uzs(Math.Max(0, spendable)) });
                }

                ledgerId = Guid.CreateVersion7(now);
                balance = await Ledger.PostAsync(c, tx, buyer.Id, allowOverdraft: false, now, new LedgerLine(
                    "purchase", -total, Describe("Покупка в баре", lines), staff.ClubId, PcId: pc?.Id, StaffId: staff.StaffId,
                    Meta: new { saleId, lines = meta }, Id: ledgerId, ShiftRequired: true, Ref: saleId.ToString()));
                shiftId = await c.ExecuteScalarAsync<Guid>("SELECT shift_id FROM ledger_entries WHERE id = @ledgerId", new { ledgerId }, tx);
                effects.Wallets.Add(buyer.Id);
            }
            else
            {
                shiftId = (await ShiftEndpoints.LockOpenShiftAsync(c, tx, staff.ClubId, strong: false)).Id;
            }

            await c.ExecuteAsync("SAVEPOINT shop_sale", transaction: tx);
            try
            {
                await c.ExecuteAsync(
                    """
                    INSERT INTO shop_sales (id, club_id, shift_id, staff_id, staff_name, kind, user_id, pc_id, method, total, ledger_id, created_at)
                    VALUES (@saleId, @ClubId, @shiftId, @StaffId, @Name, 'sale', @userId, @pcId, @method, @total, @ledgerId, @now)
                    """,
                    new { saleId, staff.ClubId, shiftId, staff.StaffId, staff.Name, userId = buyer?.Id, pcId = pc?.Id, method, total, ledgerId, now }, tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "shop_sales_pkey")
            {
                // The same cart under another key, committed while this one waited on the key: it is booked once.
                await c.ExecuteAsync("ROLLBACK TO SAVEPOINT shop_sale", transaction: tx);
                throw SaleExists(await SaleAsync(c, tx, staff.ClubId, saleId));
            }

            for (var i = 0; i < lines.Count; i++)
            {
                await c.ExecuteAsync(
                    "INSERT INTO shop_sale_lines (sale_id, line, product_id, title, qty, price) VALUES (@saleId, @line, @ProductId, @Title, @Qty, @Price)",
                    new { saleId, line = (short)(i + 1), lines[i].ProductId, lines[i].Title, lines[i].Qty, lines[i].Price }, tx);
            }

            await Audit.WriteAsync(c, tx, staff, now, "shopSale", buyer?.Id, pc?.Id, total, Summary(lines),
                new { saleId, method, lines = meta, balance }, shiftId);
            var sale = new AdminSale(
                saleId, now, shiftId, method, total, staff.Name, buyer is null ? null : new AdminSessionUser(buyer.Id, buyer.DisplayName, buyer.Role),
                pc is null ? null : new AdminOperationPc(pc.Id, pc.Name), lines);
            return new IdempotentResult(StatusCodes.Status201Created, AdminJson.ToElement(new AdminSaleResponse(
                sale, balance is { } b ? Money.Uzs(b) : null, [.. items.Select(i => sold[i.ProductId].ToWire())],
                await ShiftEndpoints.ExpectedCashAsync(c, tx, staff.ClubId))));
        }, strictBody: true);
        await sessions.PublishAsync(effects);
        return result;
    }

    /// <summary>
    /// <c>200 {void, balance, products, expectedCash}</c> (D-56): the sale row locked first (one void per sale: <c>409
    /// alreadyVoided</c>, the unique index decides a race), the window (<c>403 forbidden reason=voidWindow {minutes}</c> for a
    /// cashier after 15 min), a method sale only in its own shift (<c>409 saleShiftClosed</c>), the wallet of a balance sale,
    /// the restock (not for <c>defect</c>), the money — a balance sale gets +total back as a <c>purchase</c> row in the open shift,
    /// a cash sale's money leaves the drawer under the strong shift lock (<c>409 cashShort {available}</c>) — the void row
    /// (it joins the open shift), the journal entry <c>shopVoid</c> and the control alerts.
    /// </summary>
    private static async Task<IResult> VoidAsync(HttpContext context, string id, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions)
    {
        var staff = CounterEndpoints.StaffOnly(context);
        var saleId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("sale");
        var r = Api.Read<AdminShopVoidRequest>(body, "reasonCode");
        var reason = VoidReasons.Contains(r.ReasonCode, StringComparer.Ordinal) ? r.ReasonCode! : throw ApiException.Validation("reasonCode", "enum");
        var note = r.Note?.Trim() is { Length: > 0 } given ? given : null;
        if (note is { Length: > 200 })
        {
            throw ApiException.Validation("note", "max");
        }

        if (reason == "other" && note is not { Length: >= 3 })
        {
            throw ApiException.Validation("note", note is null ? "required" : "min");
        }

        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, ShiftEndpoints.Principal(staff), keyRequired: true, body, async (c, tx) =>
        {
            var now = sessions.Clock.GetUtcNow();
            await ShiftEndpoints.RequireOpenAsync(c, tx, staff);
            var sale = await c.QuerySingleOrDefaultAsync<SaleRow>(
                $"SELECT {SaleRow.Columns} FROM shop_sales WHERE id = @saleId AND club_id = @ClubId AND kind = 'sale' FOR UPDATE", new { saleId, staff.ClubId }, tx)
                ?? throw ApiException.NotFound("sale");
            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM shop_sales WHERE void_of = @saleId)", new { saleId }, tx))
            {
                throw SessionService.Conflict("alreadyVoided");
            }

            if (!staff.IsOwner && now - sale.CreatedAt > CashierVoidWindow)
            {
                throw new ApiException(StatusCodes.Status403Forbidden, ErrorCode.Forbidden, "A cashier voids a sale only within 15 minutes",
                    new { reason = "voidWindow", minutes = (int)CashierVoidWindow.TotalMinutes });
            }

            var fromBalance = sale.Method == "balance";
            if (!fromBalance && !await c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM shifts WHERE id = @ShiftId AND closed_at IS NULL)", new { sale.ShiftId }, tx))
            {
                throw SessionService.Conflict("saleShiftClosed");
            }

            if (fromBalance)
            {
                await c.ExecuteAsync("SELECT 1 FROM wallets WHERE user_id = @UserId FOR UPDATE", new { sale.UserId }, tx);
            }

            var lines = (await c.QueryAsync<AdminSaleLine>(
                "SELECT product_id AS \"ProductId\", title AS \"Title\", qty AS \"Qty\", price AS \"Price\", qty * price AS \"Amount\" FROM shop_sale_lines WHERE sale_id = @saleId ORDER BY line", new { saleId }, tx)).ToList();
            var products = new Dictionary<Guid, StockEndpoints.ProductRow>();
            foreach (var line in lines.OrderBy(l => l.ProductId))
            {
                var product = reason == "defect" ? null : await c.QuerySingleOrDefaultAsync<StockEndpoints.ProductRow>(
                    $"""
                    UPDATE products SET stock_qty = stock_qty + @Qty, updated_at = @now
                    WHERE id = @ProductId AND club_id = @ClubId AND deleted_at IS NULL AND stock_qty IS NOT NULL
                    RETURNING {StockEndpoints.ProductRow.Columns}
                    """,
                    new { line.ProductId, line.Qty, staff.ClubId, now }, tx);
                product ??= await c.QuerySingleOrDefaultAsync<StockEndpoints.ProductRow>(
                    $"SELECT {StockEndpoints.ProductRow.Columns} FROM products WHERE id = @ProductId AND club_id = @ClubId AND deleted_at IS NULL",
                    new { line.ProductId, staff.ClubId }, tx);
                if (product is not null)
                {
                    products[line.ProductId] = product;
                }
            }

            var voidId = Guid.CreateVersion7(now);
            Guid shiftId;
            Guid? ledgerId = null;
            long? balance = null;
            if (fromBalance)
            {
                ledgerId = Guid.CreateVersion7(now);
                balance = await Ledger.PostAsync(c, tx, sale.UserId!.Value, allowOverdraft: false, now, new LedgerLine(
                    "purchase", sale.Total, Describe("Отмена покупки в баре", lines), staff.ClubId, PcId: sale.PcId, StaffId: staff.StaffId,
                    Meta: new { voidOf = saleId }, Id: ledgerId, ShiftRequired: true, Ref: voidId.ToString()));
                shiftId = await c.ExecuteScalarAsync<Guid>("SELECT shift_id FROM ledger_entries WHERE id = @ledgerId", new { ledgerId }, tx);
                effects.Wallets.Add(sale.UserId.Value);
            }
            else
            {
                // Cash leaves the drawer: the strong lock, taken last (§4.4); other methods only keep the close out.
                var shift = await ShiftEndpoints.LockOpenShiftAsync(c, tx, staff.ClubId, strong: sale.Method == "cash");
                if (shift.Id != sale.ShiftId)
                {
                    throw SessionService.Conflict("saleShiftClosed");
                }

                if (sale.Method == "cash")
                {
                    await ShiftEndpoints.EnsureDrawerAsync(c, tx, shift, sale.Total);
                }

                shiftId = shift.Id;
            }

            await c.ExecuteAsync("SAVEPOINT shop_void", transaction: tx);
            try
            {
                await c.ExecuteAsync(
                    """
                    INSERT INTO shop_sales (id, club_id, shift_id, staff_id, staff_name, kind, void_of, user_id, pc_id, method, total, ledger_id,
                                            reason_code, note, created_at)
                    VALUES (@voidId, @ClubId, @shiftId, @StaffId, @Name, 'void', @saleId, @UserId, @PcId, @Method, @Total, @ledgerId,
                            @reason, @note, @now)
                    """,
                    new { voidId, staff.ClubId, shiftId, staff.StaffId, staff.Name, saleId, sale.UserId, sale.PcId, sale.Method, sale.Total, ledgerId, reason, note, now },
                    tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "shop_sales_one_void")
            {
                await c.ExecuteAsync("ROLLBACK TO SAVEPOINT shop_void", transaction: tx);
                throw SessionService.Conflict("alreadyVoided");
            }

            await Audit.WriteAsync(c, tx, staff, now, "shopVoid", sale.UserId, sale.PcId, sale.Total, Summary(lines),
                new
                {
                    saleId, voidId, method = sale.Method, reasonCode = reason, note, saleAt = ServerJson.FormatTime(sale.CreatedAt),
                    lines = lines.Select(l => new { productId = l.ProductId, title = l.Title, qty = l.Qty, price = l.Price }), balance,
                },
                shiftId);
            await ControlAlerts.ShopVoidAsync(c, tx, staff, shiftId, sale.Total, now);
            return new IdempotentResult(StatusCodes.Status200OK, AdminJson.ToElement(new AdminSaleVoidResponse(
                new AdminSaleVoid(voidId, now, saleId, sale.CreatedAt, sale.Method, sale.Total, reason, note, staff.Name),
                balance is { } b ? Money.Uzs(b) : null,
                [.. lines.Where(l => products.ContainsKey(l.ProductId)).Select(l => products[l.ProductId].ToWire())],
                await ShiftEndpoints.ExpectedCashAsync(c, tx, staff.ClubId))));
        }, strictBody: true);
        await sessions.PublishAsync(effects);
        return result;
    }

    /// <summary>
    /// What a player may still spend from the balance (D-55): the balance, less — with an open postpaid session — what it
    /// would charge one minute from now beyond what it already charged (a plain read: a tick in the same instant can leave
    /// a debt of at most about a minute). The member debt limit never applies to goods.
    /// </summary>
    internal static async Task<long> SpendableAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid userId, long balance, DateTimeOffset now)
    {
        var open = await c.QuerySingleOrDefaultAsync<SessionRow>(
            $"SELECT {SessionRow.Columns} FROM sessions WHERE user_id = @userId AND state <> 'ended' AND NOT is_prepaid", new { userId }, tx);
        return open is null
            ? balance
            : balance - Math.Max(0, Pricing.Frozen(open.PricePerHourSnapshot, open.Used(now) + 60, open.DayPct, open.DiscountPct) - open.ChargedTotal);
    }

    /// <summary>The cart: 1–20 lines of distinct products, 1–99 of each, else <c>400</c> naming the field.</summary>
    private static List<(Guid ProductId, int Qty)> Items(IReadOnlyList<AdminShopSaleItem?>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw ApiException.Validation("items", "required");
        }

        if (items.Count > 20)
        {
            throw ApiException.Validation("items", "max");
        }

        var cart = new List<(Guid ProductId, int Qty)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i] ?? throw ApiException.Validation($"items[{i}]", "required");
            var productId = item.ProductId ?? throw ApiException.Validation($"items[{i}].productId", "required");
            var qty = item.Qty ?? throw ApiException.Validation($"items[{i}].qty", "required");
            if (qty is < 1 or > 99)
            {
                throw ApiException.Validation($"items[{i}].qty", qty < 1 ? "min" : "max");
            }

            if (cart.Any(l => l.ProductId == productId))
            {
                throw ApiException.Validation("items", "duplicate");
            }

            cart.Add((productId, qty));
        }

        return cart;
    }

    /// <summary>
    /// One line's stock (D-54): <c>stock_qty − qty</c> (untracked stays untracked) only for a live, in-stock, sellable product
    /// with enough; else <c>404 product</c>, <c>409 notSellable {productId}</c> (category <c>time</c>) or <c>409 outOfStock
    /// {productId, available}</c> (0 when not in stock). <c>lowStock</c> follows a downward crossing.
    /// </summary>
    private static async Task<StockEndpoints.ProductRow> DecrementAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, StaffContext staff, Guid productId, int qty, DateTimeOffset now)
    {
        var after = await c.QuerySingleOrDefaultAsync<StockEndpoints.ProductRow>(
            $"""
            UPDATE products SET stock_qty = stock_qty - @qty, updated_at = @now
            WHERE id = @productId AND club_id = @ClubId AND deleted_at IS NULL AND in_stock AND category <> 'time'
              AND (stock_qty IS NULL OR stock_qty >= @qty)
            RETURNING {StockEndpoints.ProductRow.Columns}
            """,
            new { productId, staff.ClubId, qty, now }, tx);
        if (after is null)
        {
            var product = await c.QuerySingleOrDefaultAsync<(string Category, bool InStock, int? StockQty)?>(
                "SELECT category, in_stock, stock_qty FROM products WHERE id = @productId AND club_id = @ClubId AND deleted_at IS NULL",
                new { productId, staff.ClubId }, tx)
                ?? throw ApiException.NotFound("product");
            throw product.Category == "time"
                ? new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: notSellable", new { reason = "notSellable", productId })
                : new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: outOfStock",
                    new { reason = "outOfStock", productId, available = product.InStock ? product.StockQty ?? 0 : 0 });
        }

        if (after.StockQty is { } left)
        {
            await StockEndpoints.StockChangedAsync(c, tx, staff.ClubId, after.ToWire(), left + qty, now);
        }

        return after;
    }

    /// <summary>A booked sale of the club with its lines, or null.</summary>
    private static async Task<AdminSale?> SaleAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid clubId, Guid saleId)
    {
        var row = await c.QuerySingleOrDefaultAsync<SaleView>(
            """
            SELECT s.id, s.created_at, s.shift_id, s.method, s.total, s.staff_name, s.user_id, u.display_name AS user_name, u.role AS user_role,
                   s.pc_id, p.name AS pc_name
            FROM shop_sales s LEFT JOIN users u ON u.id = s.user_id LEFT JOIN pcs p ON p.id = s.pc_id
            WHERE s.id = @saleId AND s.club_id = @clubId AND s.kind = 'sale'
            """,
            new { saleId, clubId }, tx);
        if (row is null)
        {
            return null;
        }

        var lines = (await c.QueryAsync<AdminSaleLine>(
            "SELECT product_id AS \"ProductId\", title AS \"Title\", qty AS \"Qty\", price AS \"Price\", qty * price AS \"Amount\" FROM shop_sale_lines WHERE sale_id = @saleId ORDER BY line", new { saleId }, tx)).ToList();
        return new AdminSale(
            row.Id, row.CreatedAt, row.ShiftId, row.Method, row.Total, row.StaffName,
            row.UserId is { } userId ? new AdminSessionUser(userId, row.UserName ?? "", row.UserRole ?? "member") : null,
            row.PcId is { } pcId ? new AdminOperationPc(pcId, row.PcName ?? "") : null, lines);
    }

    /// <summary><c>409 conflict saleExists {sale}</c>: this cart is already booked (the sale as it was booked; null — another club's id).</summary>
    private static ApiException SaleExists(AdminSale? sale) =>
        new(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: saleExists",
            new { reason = "saleExists", sale = sale is null ? (JsonElement?)null : AdminJson.ToElement(sale) });

    /// <summary>The journal's detail of a sale: «Coca-Cola 0.5L ×2, Lay's Crab 90g».</summary>
    private static string Summary(IEnumerable<AdminSaleLine> lines) =>
        string.Join(", ", lines.Select(l => l.Qty == 1 ? l.Title : $"{l.Title} ×{l.Qty}"));

    /// <summary>The ledger row's description, which the player reads in the kiosk's wallet history (at most 200 characters).</summary>
    private static string Describe(string what, IEnumerable<AdminSaleLine> lines)
    {
        var text = $"{what}: {Summary(lines)}";
        return text.Length > 200 ? text[..199] + "…" : text;
    }

    private sealed class BuyerRow
    {
        public Guid Id { get; init; }
        public string DisplayName { get; init; } = "";
        public string Role { get; init; } = "";
        public long Balance { get; init; }
    }

    private sealed class SaleRow
    {
        public const string Columns = "id, shift_id, method, total, user_id, pc_id, created_at";

        public Guid Id { get; init; }
        public Guid ShiftId { get; init; }
        public string Method { get; init; } = "";
        public long Total { get; init; }
        public Guid? UserId { get; init; }
        public Guid? PcId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class SaleView
    {
        public Guid Id { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public Guid ShiftId { get; init; }
        public string Method { get; init; } = "";
        public long Total { get; init; }
        public string StaffName { get; init; } = "";
        public Guid? UserId { get; init; }
        public string? UserName { get; init; }
        public string? UserRole { get; init; }
        public Guid? PcId { get; init; }
        public string? PcName { get; init; }
    }
}
