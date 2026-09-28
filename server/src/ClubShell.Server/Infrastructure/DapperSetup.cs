using System.Data;
using Dapper;

namespace ClubShell.Server.Infrastructure;

/// <summary>snake_case columns to PascalCase members; Npgsql reads timestamptz as UTC <see cref="DateTime"/>, members use <see cref="DateTimeOffset"/>.</summary>
public static class DapperSetup
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1)
        {
            return;
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            // Truncated to ms (DESIGN §4): the wire format is .fffZ, and every clock value written by any slice passes here.
            parameter.DbType = DbType.DateTimeOffset;
            parameter.Value = value.ToUniversalTime().AddTicks(-(value.Ticks % TimeSpan.TicksPerMillisecond));
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset"),
        };
    }
}
