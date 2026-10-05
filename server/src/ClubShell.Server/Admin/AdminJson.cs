using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Admin;

/// <summary>
/// JSON of the admin surface (DESIGN §2.4): <see cref="ServerJson.Options"/>, but a <c>T | null</c> field is written as
/// <c>null</c>, not omitted — the console's schemas require the keys (<c>shift: null</c>, <c>closedAt: null</c>). Optional
/// non-nullable fields opt out with <c>[property: JsonIgnore(Condition = WhenWritingNull)]</c>. Every admin answer has a
/// JSON body (the console calls <c>res.json()</c>, §3.8).
/// </summary>
public static class AdminJson
{
    public static readonly JsonSerializerOptions Options = new(ServerJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static IResult Ok<T>(T value, int status = StatusCodes.Status200OK) =>
        Results.Text(JsonSerializer.Serialize(value, Options), "application/json; charset=utf-8", statusCode: status);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static readonly object OkBody = new { ok = true };
}

public sealed record AdminStaffMember(string Id, string Name, string Role, bool Active);

/// <summary>
/// X/Z report; beyond the contract: <c>topUpByMethod</c> splits the top-ups by payment method, <c>cashIn</c>/<c>cashOut</c>
/// are the drawer's movements (<c>cash_movements</c>), <c>payouts</c> the cash given back to guests, <c>apiCash</c> the cash
/// top-ups the club API key posted (never in the drawer); cash desk part 3 (D-57): <c>shop</c> is the bar net of voids,
/// <c>shopByMethod</c> splits it (the balance part may be negative in a shift that voids an earlier shift's sale),
/// <c>shopVoids</c>/<c>shopVoidCount</c> the voids made in the shift. A Z saved before those fields reads them as 0.
/// </summary>
public sealed record AdminShiftTotals(
    long TopUpCash, long TopUpOther, long Sessions, long Shop, long Refunds, long Bonuses, int Count, AdminTopUpByMethod TopUpByMethod,
    long CashIn = 0, long CashOut = 0, long Payouts = 0, long ApiCash = 0, AdminShopByMethod? ShopByMethod = null, long ShopVoids = 0,
    int ShopVoidCount = 0);

/// <summary>Top-ups of a shift by <c>ledger_entries.method</c>; <c>other</c> — rows with no method.</summary>
public sealed record AdminTopUpByMethod(long Cash, long Card, long Payme, long Click, long Uzum, long Other);

/// <summary>Bar sales of a shift by how they were paid, net of the voids made in it (D-57), in tiyin.</summary>
public sealed record AdminShopByMethod(long Cash, long Card, long Payme, long Click, long Uzum, long Balance);

/// <summary>Bar money taken today by method, net of today's voids (<c>AdminToday.shopByMethod</c>, D-57).</summary>
public sealed record AdminTodayShopByMethod(long Cash, long Card, long Payme, long Click, long Uzum);

/// <summary><c>expectedCash</c> (the drawer the close expected; null while open) and <c>closedBy</c> are beyond the contract.</summary>
public sealed record AdminShift(
    Guid Id, string StaffId, string StaffName, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, long OpeningCash, long? ClosingCash, AdminShiftTotals? Totals,
    long? ExpectedCash = null, string? ClosedBy = null);

public sealed record AdminLoginResponse(string Token, AdminStaffMember Staff, AdminShift? Shift);

public sealed record AdminMeResponse(AdminStaffMember Staff, AdminShift? Shift);

/// <summary><c>expectedCash</c> (beyond the contract): what the open shift's drawer should hold now; null with no shift.</summary>
public sealed record AdminShiftState(AdminShift? Shift, AdminShiftTotals? X, IReadOnlyList<AdminShift> History, long? ExpectedCash);

public sealed record AdminShiftResponse(AdminShift Shift);

public sealed record AdminShiftCloseResponse(AdminShift Shift, long ExpectedCash);

public sealed record AdminZone(string Name, string Color);

public sealed record AdminSeatUser(Guid Id, string DisplayName, string Role, Money Balance);

public sealed record AdminMember(Guid Id, string DisplayName, string Role, Money Balance, string Username);

/// <summary>
/// <c>signedIn</c> (beyond the contract, D-49): the session's player holds a live token on the PC; null — no session.
/// <c>game</c> (beyond the contract, D-71): the game the PC's agent reports running for this session, from its last
/// heartbeat; null — none, no session, or the PC is not busy or locked.
/// </summary>
public sealed record AdminSeat(Pc Pc, Session? Session, AdminSeatUser? User, bool? SignedIn = null, AdminSeatGame? Game = null);

/// <summary>A running game on the map (D-71): the catalog title and art; an empty <c>coverUrl</c> is null.</summary>
public sealed record AdminSeatGame(Guid Id, string Title, string? CoverUrl, string? HeroUrl);

public sealed record AdminOccupancy(int Free, int Total);

/// <summary><c>calls</c> (beyond the contract, D-63): the admin calls open or acknowledged in the last 12 h, newest first.</summary>
public sealed record AdminOverview(
    DateTimeOffset At, AdminOccupancy Club, IReadOnlyList<AdminSeat> Seats, IReadOnlyList<Tariff> Tariffs, IReadOnlyList<AdminMember> Users,
    IReadOnlyList<AdminZone> Zones, IReadOnlyList<object> Repairs, IReadOnlyList<AdminGuestDebt> GuestDebts, IReadOnlyList<AdminGuestRefund> GuestRefunds,
    IReadOnlyList<AdminCall>? Calls = null);

/// <summary>
/// An unpaid postpaid bill (beyond the contract): a guest's (<c>limits.guestPostpaid</c>) or a member's
/// (<c>limits.memberDebtLimit</c>) negative balance, to be taken at the counter (<c>adminTopUp {settleDebt}</c> of exactly
/// <c>debt</c> clears it). <c>pc</c>: the PC of the player's last session in this club.
/// </summary>
public sealed record AdminGuestDebt(Guid UserId, string DisplayName, Money Debt, string? Pc, DateTimeOffset? EndedAt, string Role);

/// <summary>
/// A walk-in guest's money left on the throwaway account after the desk ended the session (beyond the contract, D-37):
/// <c>balance</c>, and <c>payable</c> — the part the desk may give back in cash now (<c>POST /admin/wallet/payout</c>).
/// </summary>
public sealed record AdminGuestRefund(Guid UserId, string DisplayName, Money Balance, Money Payable, string? Pc, DateTimeOffset? EndedAt);

/// <summary>The player of a desk action (beyond the contract): the receipt and the settle sheet name them.</summary>
public sealed record AdminSessionUser(Guid Id, string DisplayName, string Role);

/// <summary>The money taken with an open or extend (beyond the contract): the top-up row and its tier bonus, for the receipt.</summary>
public sealed record AdminSessionPaid(Transaction Transaction, Money Bonus);

/// <summary>
/// Open: <c>balance</c>, <c>user</c>, <c>payment</c> (when paid); extend: <c>balance</c>, <c>payment</c> (when paid); end:
/// <c>refunded</c>, <c>balance</c> (after the settlement; negative — a debt), <c>user</c> and <c>payable</c> (transient guests
/// only: the cash payout allowed now). A key that does not apply is left out (optional, not nullable).
/// </summary>
public sealed record AdminSessionResult(
    Session Session,
    Money Charged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Money? Balance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Money? Refunded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdminSessionUser? User = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdminSessionPaid? Payment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Money? Payable = null);

public sealed record AdminTopUpResponse(Money Balance, Transaction Transaction, Money Bonus);

/// <summary><c>payable</c>: what is still payable after this payout (normally 0).</summary>
public sealed record AdminPayoutResponse(Money Balance, Money Payable, Transaction Transaction);

/// <summary>
/// <c>rule</c> (beyond the contract): the tariff's own refusal now (<c>tariffZone</c> | <c>tariffTime</c>), null when it can
/// be sold; <c>minutes</c>: what the price is for (a package's own minutes).
/// </summary>
public sealed record AdminPriceQuote(Money Base, int DayPct, int DiscountPct, string? DiscountReason, Money Total, string? Rule, int Minutes);

/// <summary>A drawer movement (<c>cash_movements</c>, beyond the contract); <c>amount</c> in tiyin.</summary>
public sealed record AdminCashMovement(Guid Id, string Kind, long Amount, string ReasonCode, string? Note, DateTimeOffset At, string StaffName);

public sealed record AdminCashMoveResponse(AdminCashMovement Movement, long ExpectedCash);

/// <summary>The shift an operations page belongs to.</summary>
public sealed record AdminOperationsShift(Guid Id, string StaffName, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string? ClosedBy);

public sealed record AdminOperationClient(Guid Id, string DisplayName, string Role);

public sealed record AdminOperationPc(Guid Id, string Name);

public sealed record AdminOperationQuote(long Base, int DayPct, int DiscountPct);

public sealed record AdminOperationPaid(long Amount, string Method, Guid? TransactionId);

/// <summary>
/// One desk operation of the feed (beyond the contract, D-43): a journal entry with the payment merged in. Amounts in
/// tiyin; <c>drawer</c> — its signed effect on the cash drawer. Cash desk part 3 (D-68): a bar sale (<c>shopSale</c>) and
/// its void (<c>shopVoid</c>) carry <c>saleId</c>, <c>method</c>, <c>lines</c>, <c>voided</c> (a sale) and <c>voidOfAt</c> (a
/// void: when the sale was made); a move (<c>sessionMove</c>) carries <c>fromPc</c>.
/// </summary>
public sealed record AdminOperation(
    Guid Id, DateTimeOffset At, string Kind, string StaffName, AdminOperationClient? Client, AdminOperationPc? Pc, string? Tariff, int? Minutes,
    bool? Prepaid, long Amount, long? Charged, AdminOperationQuote? Quote, AdminOperationPaid? Paid, long Drawer, string? ReasonCode, string? Note,
    Guid? SessionId, bool? Package = null, Guid? MovementId = null, Guid? SaleId = null, string? Method = null,
    IReadOnlyList<AdminOperationLine>? Lines = null, bool? Voided = null, DateTimeOffset? VoidOfAt = null, AdminOperationPc? FromPc = null);

/// <summary>A line of a bar sale as the feed and its receipt show it.</summary>
public sealed record AdminOperationLine(string Title, int Qty, long Price);

/// <summary>
/// «Сегодня» of the feed: the club's local day so far, by method, in tiyin. <c>shopByMethod</c> (D-57): the bar's method
/// money net of voids; <c>taken</c> counts it with the top-ups, <c>byMethod</c> stays top-ups only, <c>shop</c> is all goods.
/// </summary>
public sealed record AdminToday(
    string Date, DateTimeOffset From, AdminTopUpByMethod ByMethod, long Taken, long Payouts, long Sessions, long Shop,
    AdminTodayShopByMethod? ShopByMethod = null);

public sealed record AdminOperationsPage(AdminOperationsShift? Shift, IReadOnlyList<AdminOperation> Items, string? Next, AdminToday Today);

public sealed record AdminPcResponse(Pc Pc);

/// <summary><c>AdminHallPc</c>: the <c>Pc</c> fields flat plus the map's.</summary>
public sealed record AdminHallPc(
    Guid Id, string Name, string Zone, int Number, string? Hwid, string IpAddress, PcStatus Status, Guid? CurrentSessionId,
    string AgentVersion, string ShellVersion, DateTimeOffset LastHeartbeatAt, int X, int Y, string Device, JsonElement? Hardware, JsonElement? Metrics);

public sealed record AdminHallPcList(IReadOnlyList<AdminHallPc> Items, IReadOnlyList<AdminZone> Zones);

// Requests: every field nullable, so a missing one is told apart from a default (400 required).
/// <summary><c>clubCode</c> is beyond the contract: needed once the server holds more than one club (M0006).</summary>
public sealed record AdminLoginRequest(string? Pin, string? ClubCode);

/// <summary>
/// Money the cashier takes with opening or extending (beyond the contract): topped up in the action's transaction, so a
/// refused session leaves nothing booked.
/// </summary>
public sealed record AdminPayment(long? Amount, string? Method);

/// <summary><c>prepaid</c> (beyond the contract, D-30): false — postpaid from the desk; absent — prepaid, as before.</summary>
public sealed record AdminOpenSessionRequest(Guid? PcId, Guid? UserId, Guid? TariffId, int? Minutes, AdminPayment? Payment = null, bool? Prepaid = null);

/// <summary>A walk-in guest's seat (<c>POST /admin/sessions/guest</c>, beyond the contract, D-24).</summary>
public sealed record AdminGuestSessionRequest(Guid? PcId, Guid? TariffId, int? Minutes, bool? Prepaid, string? DisplayName, AdminPayment? Payment);

public sealed record AdminSessionTarget(Guid? PcId, Guid? SessionId, int? Minutes, Guid? TariffId, AdminPayment? Payment = null);

/// <summary><c>settleDebt</c> (beyond the contract, D-34): exactly the debt, no bonus.</summary>
public sealed record AdminTopUpRequest(Guid? UserId, long? Amount, string? Method, bool? SettleDebt = null);

/// <summary>A guest's cash payout (<c>POST /admin/wallet/payout</c>, beyond the contract, D-37).</summary>
public sealed record AdminPayoutRequest(Guid? UserId, long? Amount, string? Method);

/// <summary>Cash into or out of the drawer (<c>POST /admin/shift/cash</c>, beyond the contract, D-40).</summary>
public sealed record AdminCashMoveRequest(string? Kind, long? Amount, string? ReasonCode, string? Note);

public sealed record AdminPcCommandRequest(string? Kind, string? Text);

public sealed record AdminCashRequest(long? OpeningCash, long? ClosingCash);

public sealed record AdminQuoteRequest(Guid? TariffId, Guid? PcId, int? Minutes, Guid? UserId);

public sealed record AdminPcChange(string? Zone, int? Number, string? Device, string? Name, int? X, int? Y, bool? Maintenance);

// Slice S5, part A: staff, clients, promo codes, tariffs, stock.
public sealed record AdminStaffList(IReadOnlyList<AdminStaffMember> Items);

public sealed record AdminStaffCreateResponse(string Id);

/// <summary><c>AdminClient</c>: the user, the club profile and loyalty; <c>bonus</c> is always 0 (D-9), <c>telegram</c> is never sent.</summary>
public sealed record AdminClient(
    Guid Id, string Username, string DisplayName, string Role, Money Balance, Money Bonus, string? GroupId, string Note, bool Blacklisted,
    string Phone, int? BirthYear, string? CardId, long Spent, int Visits, int Level, string LevelName);

public sealed record AdminClientList(IReadOnlyList<AdminClient> Items);

/// <summary>
/// A client in the counter's picker (<c>GET /admin/clients/lookup</c>, beyond the contract): <c>phoneTail</c> — the last 4
/// digits of the club's phone (null — no phone), <c>playing</c> — the PC of the client's open session in this club,
/// <c>blacklisted</c> — the desk greys the client out (the club refuses them a session).
/// </summary>
public sealed record AdminClientLookupItem(
    Guid Id, string DisplayName, string Username, string? PhoneTail, Money Balance, Money Bonus, string? CardId, AdminClientPlaying? Playing,
    bool Blacklisted);

public sealed record AdminClientPlaying(Guid PcId, string PcName);

public sealed record AdminClientLookupList(IReadOnlyList<AdminClientLookupItem> Items);

public sealed record AdminClientResponse(AdminClient Client);

public sealed record AdminTransactionList(IReadOnlyList<Transaction> Items);

public sealed record AdminPromoRedeemResponse(Money Balance);

public sealed record AdminTariffList(IReadOnlyList<Tariff> Items);

public sealed record AdminTariffResponse(Tariff Tariff);

public sealed record AdminProductList(IReadOnlyList<Contracts.Shop.Product> Items, int LowAt);

public sealed record AdminProductResponse(Contracts.Shop.Product Product);

public sealed record AdminStaffCreateRequest(string? Name, string? Role, string? Pin);

public sealed record AdminStaffUpdateRequest(string? Name, bool? Active, string? Pin);

/// <summary><c>telegram</c> is prohibited: not a member, so the binder skips it (accepted and ignored).</summary>
public sealed record AdminClientCreateRequest(
    string? DisplayName, string? Username, string? Phone, int? BirthYear, string? GroupId, string? Password, string? CardId);

public sealed record AdminClientUpdateRequest(string? DisplayName, string? GroupId, string? Note, string? Phone, int? BirthYear, bool? Blacklisted);

public sealed record AdminClientCardRequest(string? CardId);

public sealed record AdminClientPasswordRequest(string? Password);

public sealed record AdminPromoRedeemRequest(Guid? UserId, string? Code);

public sealed record AdminTariffInput(
    string? Name, long? PricePerHour, int? MinMinutes, int? MaxMinutes, IReadOnlyList<string>? Zones, JsonElement? TimeWindows, bool? IsPackage,
    int? PackageMinutes, long? PackagePrice);

/// <summary><c>expectedStockQty</c> (beyond the contract, D-54): the quantity the panel showed; checked only with <c>stockQty</c>.</summary>
public sealed record AdminProductUpdateRequest(string? Title, long? Price, bool? InStock, int? StockQty, int? ExpectedStockQty = null);

public sealed record AdminProductReceiveRequest(int? Qty);

// Cash desk part 3 (D-52..D-70), all beyond the contract.

/// <summary>A product the owner adds at the desk (<c>POST /admin/products</c>, D-58).</summary>
public sealed record AdminProductCreateRequest(string? Title, string? Category, long? Price, int? StockQty, bool? InStock);

public sealed record AdminShopSaleItem(Guid? ProductId, int? Qty);

/// <summary>A bar sale (<c>POST /admin/shop/sales</c>, D-52): <c>payment</c> — paid by a method; without it — from <c>userId</c>'s balance.</summary>
public sealed record AdminShopSaleRequest(Guid? SaleId, IReadOnlyList<AdminShopSaleItem?>? Items, long? Total, Guid? UserId, Guid? PcId, AdminPayment? Payment);

public sealed record AdminShopVoidRequest(string? ReasonCode, string? Note);

/// <summary>A line of a bar sale: <c>price</c> per unit at the sale, <c>amount</c> = qty × price; tiyin.</summary>
public sealed record AdminSaleLine(Guid ProductId, string Title, int Qty, long Price, long Amount);

/// <summary>
/// A bar sale: <c>method</c> <c>cash | card | payme | click | uzum | balance</c>, <c>total</c> in tiyin, <c>user</c> the buyer
/// named (a member or a guest; null — a walk-in), <c>pc</c> the PC it was brought to.
/// </summary>
public sealed record AdminSale(
    Guid Id, DateTimeOffset At, Guid ShiftId, string Method, long Total, string StaffName, AdminSessionUser? User, AdminOperationPc? Pc,
    IReadOnlyList<AdminSaleLine> Lines);

/// <summary><c>balance</c>: after a balance sale, else null; <c>products</c>: each line's product after the sale.</summary>
public sealed record AdminSaleResponse(AdminSale Sale, Money? Balance, IReadOnlyList<Contracts.Shop.Product> Products, long ExpectedCash);

public sealed record AdminSaleVoid(
    Guid Id, DateTimeOffset At, Guid SaleId, DateTimeOffset SaleAt, string Method, long Total, string ReasonCode, string? Note, string StaffName);

/// <summary><c>balance</c>: after a balance void, else null; <c>products</c>: the sale's products after the restock.</summary>
public sealed record AdminSaleVoidResponse(AdminSaleVoid Void, Money? Balance, IReadOnlyList<Contracts.Shop.Product> Products, long ExpectedCash);

/// <summary>A session moved to another PC (<c>POST /admin/sessions/move</c>, D-59).</summary>
public sealed record AdminMoveRequest(Guid? FromPcId, Guid? SessionId, Guid? ToPcId, Guid? TariffId);

public sealed record AdminMovePc(Guid PcId, string Name);

/// <summary><c>signedIn</c> is always false: the player signs in on the target PC («ждёт входа»).</summary>
public sealed record AdminMoveResponse(Session Session, AdminMovePc From, AdminMovePc To, bool TariffChanged, AdminSessionUser User, bool SignedIn);

public sealed record AdminCallUser(Guid Id, string DisplayName);

/// <summary>
/// An admin call of the desk inbox (D-62, D-63): <c>category</c> <c>help | technical | order | other | problem</c> (a «report
/// a problem» text), <c>source</c> <c>direct | telemetry | report</c>, <c>at</c> — when the server received it, <c>repeat</c> — a
/// press shortly after «Иду», stored acknowledged (it does not ring again).
/// </summary>
public sealed record AdminCall(
    Guid Id, Guid PcId, string PcName, int PcNumber, AdminCallUser? User, string Category, string? Message, string Source, DateTimeOffset At,
    string Status, bool Repeat, string? AckedBy, DateTimeOffset? AckedAt);

public sealed record AdminCallAckRequest(bool? Notify);

/// <summary>
/// <c>notified</c>: «Администратор идёт к вам» went to the connected PC (true) or the PC was not connected (false); null when
/// this request answered nothing — the call was already answered or closed (on another console), and nothing was sent again.
/// </summary>
public sealed record AdminCallAckResponse(AdminCall Call, bool? Notified);

public sealed record AdminCallResponse(AdminCall Call);

/// <summary>
/// One command to many PCs (<c>POST /admin/pcs/commands</c>, D-65). <c>sessionIds</c> (with <c>includeBusy</c>): the sessions
/// the desk's confirm listed — only those are ended; another session open on a selected PC is <c>skipped sessionOpen</c>.
/// </summary>
public sealed record AdminBulkCommandRequest(
    IReadOnlyList<Guid>? PcIds, string? Kind, string? Text, string? Level, bool? IncludeBusy, IReadOnlyList<Guid>? SessionIds);

/// <summary>A session a bulk reboot or shutdown ended first (<c>includeBusy</c>), as the desk's «Завершить» would.</summary>
public sealed record AdminBulkEnded(Guid SessionId, AdminSessionUser User, Money Charged, Money Refunded);

/// <summary>
/// <c>outcome</c> <c>done | queued | noAnswer | failed | skipped</c>; <c>skipped</c> <c>sessionOpen | offline | notFound</c>
/// when not sent; <c>ack</c> the PC's answer (a failure carries the refusal).
/// </summary>
public sealed record AdminBulkCommandResult(Guid PcId, string? PcName, string Outcome, string? Skipped, CommandAck? Ack, AdminBulkEnded? Ended);

public sealed record AdminBulkCommandResponse(Guid BatchId, IReadOnlyList<AdminBulkCommandResult> Results);
