using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;

namespace Raffaello.Server.Auth;

public sealed class UserRow
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string WindowsAccount { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public UserDto ToDto() => new() { Id = Id, UserName = UserName, DisplayName = DisplayName, Role = Role, WindowsAccount = WindowsAccount, Active = Active, LastLoginAt = LastLoginAt };
}

/// <summary>
/// App accounts (BCrypt-hashed passwords) and Windows accounts mapped to roles, plus bearer-token sessions.
/// Tokens are random 256-bit values; only their SHA-256 is stored, so a database dump does not leak usable tokens.
/// </summary>
public sealed class UserStore
{
    private readonly NpgsqlDataSource _ds;
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromDays(30);
    public int BcryptWorkFactor { get; init; } = 11;

    public UserStore(NpgsqlDataSource ds) => _ds = ds;

    private PgTx Open(out NpgsqlConnection c) { c = _ds.OpenConnection(); return new PgTx(c, null); }

    public List<UserRow> All()
    {
        var p = Open(out var c); using (c) return p.Query<UserRow>("""SELECT * FROM "Users" ORDER BY "UserName" """);
    }

    public UserRow? Find(string userName)
    {
        var p = Open(out var c);
        using (c) return p.Query<UserRow>("""SELECT * FROM "Users" WHERE lower("UserName") = lower(@u)""", ("u", userName.Trim())).FirstOrDefault();
    }

    public UserRow? FindById(long id)
    {
        var p = Open(out var c);
        using (c) return p.Query<UserRow>("""SELECT * FROM "Users" WHERE "Id" = @id""", ("id", id)).FirstOrDefault();
    }

    public UserRow? FindWindows(string account)
    {
        var p = Open(out var c);
        using (c) return p.Query<UserRow>("""SELECT * FROM "Users" WHERE lower("WindowsAccount") = lower(@a) OR (lower("UserName") = lower(@a))""", ("a", account.Trim())).FirstOrDefault();
    }

    public UserRow Create(string userName, string role, string? password, string displayName = "", string windowsAccount = "")
    {
        var r = Roles.Normalize(role);
        if (r.Length == 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"Role must be one of {string.Join(", ", Roles.All)}.");
        if (string.IsNullOrWhiteSpace(userName)) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "User name is required.");
        if (password != null) ValidatePassword(password);
        if (Find(userName) != null) throw new WriteRejectedException(409, ErrorCodes.Conflict, $"User {userName} already exists.");
        var p = Open(out var c);
        using (c)
        {
            var id = Convert.ToInt64(p.Scalar("""
                INSERT INTO "Users" ("UserName", "DisplayName", "Role", "PasswordHash", "WindowsAccount", "Active", "CreatedAt")
                VALUES (@u, @d, @r, @h, @w, true, @at) RETURNING "Id"
                """, ("u", userName.Trim()), ("d", string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim()), ("r", r),
                ("h", password is null ? null : BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor)), ("w", windowsAccount.Trim()), ("at", DateTime.Now)), CultureInfo.InvariantCulture);
            return FindById(id)!;
        }
    }

    public UserRow Update(long id, UserDto dto)
    {
        var u = FindById(id) ?? throw new WriteRejectedException(404, ErrorCodes.NotFound, $"User #{id} not found.");
        var role = dto.Role.Length > 0 ? Roles.Normalize(dto.Role) : u.Role;
        if (role.Length == 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"Role must be one of {string.Join(", ", Roles.All)}.");
        var hash = u.PasswordHash;
        if (!string.IsNullOrEmpty(dto.Password)) { ValidatePassword(dto.Password); hash = BCrypt.Net.BCrypt.HashPassword(dto.Password, BcryptWorkFactor); }
        var p = Open(out var c);
        using (c)
        {
            p.Exec("""
                UPDATE "Users" SET "DisplayName" = @d, "Role" = @r, "PasswordHash" = @h, "WindowsAccount" = @w, "Active" = @a WHERE "Id" = @id
                """, ("d", dto.DisplayName.Length > 0 ? dto.DisplayName : u.DisplayName), ("r", role), ("h", hash),
                ("w", dto.WindowsAccount ?? u.WindowsAccount), ("a", dto.Active), ("id", id));
            if (!dto.Active || hash != u.PasswordHash) p.Exec("""DELETE FROM "Sessions" WHERE "UserId" = @id""", ("id", id));
        }
        return FindById(id)!;
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < 8) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "Passwords need at least 8 characters.");
    }

    /// <summary>Checks the password (constant-time BCrypt) and issues a session token.</summary>
    public LoginResponse? Login(string userName, string password, string machine)
    {
        var u = Find(userName);
        if (u is null || !u.Active || string.IsNullOrEmpty(u.PasswordHash) || !BCrypt.Net.BCrypt.Verify(password, u.PasswordHash))
        {
            if (u is null) BCrypt.Net.BCrypt.HashPassword("timing-equaliser", 4);
            return null;
        }
        return IssueToken(u, machine);
    }

    public LoginResponse IssueToken(UserRow u, string machine)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTime.Now.Add(TokenLifetime);
        var p = Open(out var c);
        using (c)
        {
            p.Exec("""INSERT INTO "Sessions" ("TokenHash", "UserId", "CreatedAt", "ExpiresAt", "Machine") VALUES (@h, @u, @c, @e, @m)""",
                ("h", Hash(token)), ("u", u.Id), ("c", DateTime.Now), ("e", expires), ("m", machine ?? ""));
            p.Exec("""UPDATE "Users" SET "LastLoginAt" = @at WHERE "Id" = @id""", ("at", DateTime.Now), ("id", u.Id));
            p.Exec("""DELETE FROM "Sessions" WHERE "ExpiresAt" < @now""", ("now", DateTime.Now));
        }
        return new LoginResponse { Token = token, UserName = u.UserName, DisplayName = u.DisplayName, Role = u.Role, ExpiresAt = expires };
    }

    public UserRow? ValidateToken(string token)
    {
        var p = Open(out var c);
        using (c)
            return p.Query<UserRow>("""
                SELECT u.* FROM "Sessions" s JOIN "Users" u ON u."Id" = s."UserId"
                WHERE s."TokenHash" = @h AND s."ExpiresAt" > @now AND u."Active"
                """, ("h", Hash(token)), ("now", DateTime.Now)).FirstOrDefault();
    }

    public void Revoke(string token)
    {
        var p = Open(out var c);
        using (c) p.Exec("""DELETE FROM "Sessions" WHERE "TokenHash" = @h""", ("h", Hash(token)));
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
