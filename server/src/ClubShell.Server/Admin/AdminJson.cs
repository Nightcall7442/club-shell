using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>X/Z report; <c>topUpByMethod</c> (beyond the contract) splits the top-ups by payment method.</summary>
public sealed record AdminShiftTotals(
    long TopUpCash, long TopUpOther, long Sessions, long Shop, long Refunds, long Bonuses, int Count, AdminTopUpByMethod TopUpByMethod);

/// <summary>Top-ups of a shift by <c>ledger_entries.method</c>; <c>other</c> — rows with no method.</summary>
public sealed record AdminTopUpByMethod(long Cash, long Card, long Payme, long Click, long Uzum, long Other);

public sealed record AdminShift(
    Guid Id, string StaffId, string StaffName, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, long OpeningCash, long? ClosingCash, AdminShiftTotals? Totals);

public sealed record AdminLoginResponse(string Token, AdminStaffMember Staff, AdminShift? Shift);

public sealed record AdminMeResponse(AdminStaffMember Staff, AdminShift? Shift);

public sealed record AdminShiftState(AdminShift? Shift, AdminShiftTotals? X, IReadOnlyList<AdminShift> History);

public sealed record AdminShiftResponse(AdminShift Shift);

public sealed record AdminShiftCloseResponse(AdminShift Shift, long ExpectedCash);

public sealed record AdminZone(string Name, string Color);

public sealed record AdminSeatUser(Guid Id, string DisplayName, string Role, Money Balance);

public sealed record AdminMember(Guid Id, string DisplayName, string Role, Money Balance, string Username);

public sealed record AdminSeat(Pc Pc, Session? Session, AdminSeatUser? User);

public sealed record AdminOccupancy(int Free, int Total);

public sealed record AdminOverview(
    DateTimeOffset At, AdminOccupancy Club, IReadOnlyList<AdminSeat> Seats, IReadOnlyList<Tariff> Tariffs, IReadOnlyList<AdminMember> Users,
    IReadOnlyList<AdminZone> Zones, IReadOnlyList<object> Repairs, IReadOnlyList<AdminGuestDebt> GuestDebts);

/// <summary>
/// An unpaid postpaid bill of a guest (beyond the contract, <c>limits.guestPostpaid</c>): the guest account's negative
/// balance, to be taken at the counter (a top-up of <c>debt</c> clears it). <c>pc</c>: the PC of the guest's last session.
/// </summary>
public sealed record AdminGuestDebt(Guid UserId, string DisplayName, Money Debt, string? Pc, DateTimeOffset? EndedAt);

/// <summary>Open/extend: <c>balance</c>; end: <c>refunded</c> — the other key is left out (optional, not nullable).</summary>
public sealed record AdminSessionResult(
    Session Session,
    Money Charged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Money? Balance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Money? Refunded = null);

public sealed record AdminTopUpResponse(Money Balance, Transaction Transaction, Money Bonus);

public sealed record AdminPriceQuote(Money Base, int DayPct, int DiscountPct, string? DiscountReason, Money Total);

public sealed record AdminPcResponse(Pc Pc);

/// <summary><c>AdminHallPc</c>: the <c>Pc</c> fields flat plus the map's.</summary>
public sealed record AdminHallPc(
    Guid Id, string Name, string Zone, int Number, string? Hwid, string IpAddress, PcStatus Status, Guid? CurrentSessionId,
    string AgentVersion, string ShellVersion, DateTimeOffset LastHeartbeatAt, int X, int Y, string Device, JsonElement? Hardware, JsonElement? Metrics);

public sealed record AdminHallPcList(IReadOnlyList<AdminHallPc> Items, IReadOnlyList<AdminZone> Zones);

// Requests: every field nullable, so a missing one is told apart from a default (400 required).
/// <summary><c>clubCode</c> is beyond the contract: needed once the server holds more than one club (M0006).</summary>
public sealed record AdminLoginRequest(string? Pin, string? ClubCode);

public sealed record AdminOpenSessionRequest(Guid? PcId, Guid? UserId, Guid? TariffId, int? Minutes);

public sealed record AdminSessionTarget(Guid? PcId, Guid? SessionId, int? Minutes, Guid? TariffId);

public sealed record AdminTopUpRequest(Guid? UserId, long? Amount, string? Method);

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
/// digits of the club's phone (null — no phone), <c>playing</c> — the PC of the client's open session in this club.
/// </summary>
public sealed record AdminClientLookupItem(
    Guid Id, string DisplayName, string Username, string? PhoneTail, Money Balance, Money Bonus, string? CardId, AdminClientPlaying? Playing);

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

public sealed record AdminProductUpdateRequest(string? Title, long? Price, bool? InStock, int? StockQty);

public sealed record AdminProductReceiveRequest(int? Qty);
