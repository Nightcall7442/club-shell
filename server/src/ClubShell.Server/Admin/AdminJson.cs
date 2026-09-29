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

public sealed record AdminShiftTotals(long TopUpCash, long TopUpOther, long Sessions, long Shop, long Refunds, long Bonuses, int Count);

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
    IReadOnlyList<AdminZone> Zones, IReadOnlyList<object> Repairs);

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
public sealed record AdminLoginRequest(string? Pin);

public sealed record AdminOpenSessionRequest(Guid? PcId, Guid? UserId, Guid? TariffId, int? Minutes);

public sealed record AdminSessionTarget(Guid? PcId, Guid? SessionId, int? Minutes, Guid? TariffId);

public sealed record AdminTopUpRequest(Guid? UserId, long? Amount, string? Method);

public sealed record AdminPcCommandRequest(string? Kind, string? Text);

public sealed record AdminCashRequest(long? OpeningCash, long? ClosingCash);

public sealed record AdminQuoteRequest(Guid? TariffId, Guid? PcId, int? Minutes, Guid? UserId);

public sealed record AdminPcChange(string? Zone, int? Number, string? Device, string? Name, int? X, int? Y, bool? Maintenance);
