using System.Data;
using System.Globalization;
using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace Raffaello.Server.Data;

/// <summary>Raised by a guard or the store; the API turns it into an HTTP status + <see cref="Core.Remote.ErrorDto"/>.</summary>
public sealed class WriteRejectedException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public WriteRejectedException(int status, string code, string message) : base(message) { Status = status; Code = code; }
}

/// <summary>CLR property type to PostgreSQL column type / parameter, and back. Identifiers are always quoted (PascalCase, "User" ...).</summary>
public static class PgMap
{
    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string SqlType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(long) || t.IsEnum) return "bigint";
        if (t == typeof(int) || t == typeof(short)) return "integer";
        if (t == typeof(bool)) return "boolean";
        if (t == typeof(double) || t == typeof(float)) return "double precision";
        if (t == typeof(decimal)) return "numeric";
        if (t == typeof(DateTime)) return "timestamp without time zone";
        if (t == typeof(byte[])) return "bytea";
        if (t == typeof(Guid)) return "uuid";
        return "text";
    }

    private static NpgsqlDbType DbType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(long) || t.IsEnum) return NpgsqlDbType.Bigint;
        if (t == typeof(int) || t == typeof(short)) return NpgsqlDbType.Integer;
        if (t == typeof(bool)) return NpgsqlDbType.Boolean;
        if (t == typeof(double) || t == typeof(float)) return NpgsqlDbType.Double;
        if (t == typeof(decimal)) return NpgsqlDbType.Numeric;
        if (t == typeof(DateTime)) return NpgsqlDbType.Timestamp;
        if (t == typeof(byte[])) return NpgsqlDbType.Bytea;
        if (t == typeof(Guid)) return NpgsqlDbType.Uuid;
        return NpgsqlDbType.Text;
    }

    public static NpgsqlParameter Param(string name, object? value, Type declared)
    {
        var p = new NpgsqlParameter(name, DbType(declared)) { Value = ToDb(value) };
        return p;
    }

    /// <summary>Parameter for an ad-hoc value (type from the value).</summary>
    public static NpgsqlParameter Param(string name, object? value) =>
        value is null ? new NpgsqlParameter(name, DBNull.Value) : Param(name, value, value.GetType());

    public static object ToDb(object? v) => v switch
    {
        null => DBNull.Value,
        DateTime dt => DateTime.SpecifyKind(dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt, DateTimeKind.Unspecified),
        Enum en => Convert.ToInt64(en, CultureInfo.InvariantCulture),
        float f => (double)f,
        short s => (int)s,
        _ => v,
    };

    public static object? FromDb(object v, Type target)
    {
        if (v is DBNull) return null;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t == typeof(string)) return Convert.ToString(v, CultureInfo.InvariantCulture);
        if (t == typeof(byte[])) return v as byte[];
        if (t == typeof(DateTime)) return v is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Unspecified) : Convert.ToDateTime(v, CultureInfo.InvariantCulture);
        if (t == typeof(Guid)) return v is Guid g ? g : Guid.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!);
        if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(v, CultureInfo.InvariantCulture));
        if (t == typeof(bool)) return v is bool b ? b : Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0;
        return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
    }

    public static List<T> Read<T>(NpgsqlDataReader r) where T : new() => Read(r, typeof(T)).Cast<T>().ToList();

    public static List<object> Read(NpgsqlDataReader r, Type type)
    {
        var props = EntityRegistryProps(type).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var map = new PropertyInfo?[r.FieldCount];
        for (var i = 0; i < r.FieldCount; i++) map[i] = props.GetValueOrDefault(r.GetName(i));
        var list = new List<object>();
        while (r.Read())
        {
            var o = Activator.CreateInstance(type)!;
            for (var i = 0; i < map.Length; i++)
            {
                var p = map[i];
                if (p is null) continue;
                p.SetValue(o, FromDb(r.GetValue(i), p.PropertyType));
            }
            list.Add(o);
        }
        return list;
    }

    private static PropertyInfo[] EntityRegistryProps(Type t) => EntityRegistry.Props(t);
}

/// <summary>An open connection + transaction, with small helpers for guards and the store.</summary>
public sealed class PgTx
{
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction? Transaction { get; }

    public PgTx(NpgsqlConnection c, NpgsqlTransaction? tx) { Connection = c; Transaction = tx; }

    public NpgsqlCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, Connection, Transaction);
        foreach (var (n, v) in args) cmd.Parameters.Add(PgMap.Param(n, v));
        return cmd;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public List<T> Query<T>(string sql, params (string Name, object? Value)[] args) where T : new()
    {
        using var cmd = Command(sql, args);
        using var r = cmd.ExecuteReader();
        return PgMap.Read<T>(r);
    }

    public List<object> Query(Type t, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        using var r = cmd.ExecuteReader();
        return PgMap.Read(r, t);
    }

    /// <summary>Transaction-scoped advisory lock on a text key (released at commit / rollback).</summary>
    public void Lock(string key) => Exec("SELECT pg_advisory_xact_lock(hashtextextended(@k, 7))", ("k", key));
}
