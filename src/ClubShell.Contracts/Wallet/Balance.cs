using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClubShell.Contracts.Wallet;

/// <summary>
/// Monetary amount in integer minor units (tiyin for UZS). Serialized by <see cref="MoneyJsonConverter"/> as
/// <c>{ "amount": 123, "currency": "UZS" }</c>. <c>default(Money)</c> equals <see cref="Zero"/>.
/// Arithmetic and comparison across currencies throw <see cref="InvalidOperationException"/>.
/// </summary>
/// <param name="Amount">Signed minor units (negative for charges/purchases where noted).</param>
/// <param name="Currency">ISO-4217 code; normalized to upper-case; <see cref="DefaultCurrency"/> when empty.</param>
[JsonConverter(typeof(MoneyJsonConverter))]
public readonly record struct Money(long Amount, string Currency) : IComparable<Money>, IComparable
{
    /// <summary>Currency assumed when none is given.</summary>
    public const string DefaultCurrency = "UZS";

    /// <summary>Zero in <see cref="DefaultCurrency"/>.</summary>
    public static readonly Money Zero = new(0, DefaultCurrency);

    // Stores null for the default currency so that default(Money) == Zero and equality is canonical.
    private readonly string? _currency = Normalize(Currency);

    /// <summary>ISO-4217 currency code (never null or empty).</summary>
    public string Currency
    {
        get => _currency ?? DefaultCurrency;
        init => _currency = Normalize(value);
    }

    /// <summary><see langword="true"/> when <see cref="Amount"/> is 0.</summary>
    public bool IsZero => Amount == 0;

    /// <summary><see langword="true"/> when <see cref="Amount"/> is negative.</summary>
    public bool IsNegative => Amount < 0;

    /// <summary><see langword="true"/> when <see cref="Amount"/> is positive.</summary>
    public bool IsPositive => Amount > 0;

    /// <summary>Creates an amount in <see cref="DefaultCurrency"/>.</summary>
    public static Money Uzs(long amount) => new(amount, DefaultCurrency);

    /// <summary>Creates an amount in <paramref name="currency"/>.</summary>
    public static Money Of(long amount, string currency = DefaultCurrency) => new(amount, currency);

    /// <summary>Absolute value.</summary>
    public Money Abs() => Amount < 0 ? this with { Amount = checked(-Amount) } : this;

    /// <summary>Negated value.</summary>
    public Money Negate() => this with { Amount = checked(-Amount) };

    /// <summary><see langword="true"/> when both amounts are denominated in the same currency.</summary>
    public bool IsSameCurrency(Money other) => string.Equals(Currency, other.Currency, StringComparison.Ordinal);

    /// <summary>Sum; throws when currencies differ.</summary>
    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left with { Amount = checked(left.Amount + right.Amount) };
    }

    /// <summary>Difference; throws when currencies differ.</summary>
    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left with { Amount = checked(left.Amount - right.Amount) };
    }

    /// <summary>Unary negation.</summary>
    public static Money operator -(Money value) => value.Negate();

    /// <summary>Scales by an integer factor (e.g. unit price × quantity).</summary>
    public static Money operator *(Money value, long factor) => value with { Amount = checked(value.Amount * factor) };

    /// <summary>Scales by an integer factor.</summary>
    public static Money operator *(long factor, Money value) => value * factor;

    /// <summary>Less-than; throws when currencies differ.</summary>
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    /// <summary>Greater-than; throws when currencies differ.</summary>
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    /// <summary>Less-or-equal; throws when currencies differ.</summary>
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-or-equal; throws when currencies differ.</summary>
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>Named form of the addition operator.</summary>
    public static Money Add(Money left, Money right) => left + right;

    /// <summary>Named form of the subtraction operator.</summary>
    public static Money Subtract(Money left, Money right) => left - right;

    /// <summary>Named form of the scaling operator.</summary>
    public static Money Multiply(Money value, long factor) => value * factor;

    /// <summary>Sums a sequence; an empty sequence yields <see cref="Zero"/>.</summary>
    public static Money Sum(IEnumerable<Money> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Money? total = null;
        foreach (var v in values)
        {
            total = total is null ? v : total.Value + v;
        }

        return total ?? Zero;
    }

    /// <inheritdoc />
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        Money m => CompareTo(m),
        _ => throw new ArgumentException($"Object must be of type {nameof(Money)}", nameof(obj)),
    };

    /// <summary>Formats as <c>&lt;amount&gt; &lt;currency&gt;</c> in minor units, invariant culture (e.g. <c>1500000 UZS</c>).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount} {Currency}");

    private static string? Normalize(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        var upper = currency.Trim().ToUpperInvariant();
        return string.Equals(upper, DefaultCurrency, StringComparison.Ordinal) ? null : upper;
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!left.IsSameCurrency(right))
        {
            throw new InvalidOperationException($"Currency mismatch: {left.Currency} vs {right.Currency}");
        }
    }
}

/// <summary>
/// JSON converter for <see cref="Money"/>: <c>{ "amount": &lt;int64&gt;, "currency": "&lt;ISO-4217&gt;" }</c>.
/// Property names are matched case-insensitively on read; unknown members are ignored; <c>amount</c> is required.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    private static readonly JsonEncodedText AmountName = JsonEncodedText.Encode("amount");
    private static readonly JsonEncodedText CurrencyName = JsonEncodedText.Encode("currency");

    /// <inheritdoc />
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Money must be a JSON object");
        }

        long? amount = null;
        string? currency = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Malformed Money object");
            }

            if (reader.ValueTextEquals("amount"u8) || IsName(ref reader, "amount"))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
                {
                    throw new JsonException("Money.amount must be an integer");
                }

                amount = value;
            }
            else if (reader.ValueTextEquals("currency"u8) || IsName(ref reader, "currency"))
            {
                if (!reader.Read())
                {
                    throw new JsonException("Malformed Money object");
                }

                currency = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Null => null,
                    _ => throw new JsonException("Money.currency must be a string"),
                };
            }
            else
            {
                if (!reader.Read())
                {
                    throw new JsonException("Malformed Money object");
                }

                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("Unterminated Money object");
        }

        if (amount is null)
        {
            throw new JsonException("Money.amount is required");
        }

        return new Money(amount.Value, currency ?? Money.DefaultCurrency);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(AmountName, value.Amount);
        writer.WriteString(CurrencyName, value.Currency);
        writer.WriteEndObject();
    }

    private static bool IsName(ref Utf8JsonReader reader, string name) =>
        string.Equals(reader.GetString(), name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Wallet balance of a user (IPC_PROTOCOL.md §6.12). Payload of <c>wallet.balance</c> and the <c>wallet.updated</c> event.</summary>
/// <param name="UserId">Owner.</param>
/// <param name="Amount">Main (real money) balance.</param>
/// <param name="Bonus">Bonus balance (promotional, non-withdrawable).</param>
/// <param name="Currency">ISO-4217 code of both balances.</param>
/// <param name="UpdatedAt">Last change time; stale when served from cache while offline.</param>
public sealed record Balance(
    Guid UserId,
    Money Amount,
    Money Bonus,
    string Currency,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Main + bonus.</summary>
    [JsonIgnore]
    public Money Total => Amount + Bonus;
}
