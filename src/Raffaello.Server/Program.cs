using Npgsql;
using Raffaello.Core.Remote;
using Raffaello.Server;
using Raffaello.Server.Auth;
using Raffaello.Server.Backup;
using Raffaello.Server.Data;

return await ServerCli.RunAsync(args);

namespace Raffaello.Server
{
    /// <summary>
    /// Raffaello.Server [run]                       start the API (also how the Windows service starts it)
    /// Raffaello.Server migrate                     create the database if missing + apply migrations
    /// Raffaello.Server backup [--to FOLDER]        pg_dump now (same as the nightly job)
    /// Raffaello.Server restore FILE [--yes]        pg_restore a dump over the database (stop the service first)
    /// Raffaello.Server adduser NAME ROLE [PASSWORD] [--windows DOMAIN\user]
    /// Raffaello.Server passwd NAME PASSWORD
    /// Raffaello.Server users | url | help
    /// </summary>
    public static class ServerCli
    {
        private static readonly string[] Commands = { "run", "migrate", "backup", "restore", "adduser", "passwd", "users", "url", "help", "--help", "-h", "/?" };

        public static async Task<int> RunAsync(string[] args)
        {
            var cmd = args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase) ? args[0].ToLowerInvariant() : "run";
            var rest = cmd == "run" && args.Length > 0 && !args[0].Equals("run", StringComparison.OrdinalIgnoreCase) ? args : args.Skip(1).ToArray();
            if (cmd == "run")
            {
                var app = ServerApp.Build(rest);
                await app.RunAsync();
                return 0;
            }
            try
            {
                return Run(cmd, rest);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static int Run(string cmd, string[] args)
        {
            var config = ServerApp.LoadConfig(args);
            var opt = config.GetSection("Raffaello").Get<ServerOptions>() ?? new ServerOptions();
            var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            string? Flag(string name)
            {
                var i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            bool Has(string name) => args.Any(a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));

            switch (cmd)
            {
                case "help": case "--help": case "-h": case "/?":
                    Console.WriteLine("""
                        Raffaello.Server [run]                         start the server
                        Raffaello.Server migrate                       create the database (if missing) and apply migrations
                        Raffaello.Server backup [--to FOLDER]          back up now (pg_dump)
                        Raffaello.Server restore FILE [--yes]          restore a backup (stop the service first)
                        Raffaello.Server adduser NAME ROLE [PASSWORD] [--windows DOMAIN\user]   ROLE = SITE | QS | REVIEWER | ADMIN
                        Raffaello.Server passwd NAME PASSWORD          set a new password
                        Raffaello.Server users                         list users
                        Raffaello.Server url                           print the address users type in Settings
                        """);
                    return 0;
                case "url":
                    foreach (var u in ServerApp.ListenUrls(opt)) Console.WriteLine(u);
                    return 0;
            }

            var cs = ServerApp.ConnectionString(config);
            switch (cmd)
            {
                case "migrate":
                {
                    var created = ServerApp.EnsureDatabase(cs);
                    using var ds = NpgsqlDataSource.Create(cs);
                    PgSchema.Migrate(ds);
                    Console.WriteLine(created ? "Database created and migrated." : "Database migrated (up to date).");
                    return 0;
                }
                case "backup":
                {
                    var to = Flag("to");
                    if (to != null) opt.BackupFolder = to;
                    var runner = new BackupRunner(cs, opt, AppContext.BaseDirectory);
                    var file = runner.Backup();
                    Console.WriteLine($"Backup written: {file}");
                    return 0;
                }
                case "restore":
                {
                    if (positional.Count < 1) throw new ArgumentException("restore needs the backup file.");
                    if (!Has("yes"))
                    {
                        Console.Write($"This REPLACES all data in the database with {positional[0]}. Type YES to continue: ");
                        if (!string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal)) { Console.WriteLine("Cancelled."); return 2; }
                    }
                    new BackupRunner(cs, opt, AppContext.BaseDirectory).Restore(positional[0]);
                    Console.WriteLine("Restored. Start the service again.");
                    return 0;
                }
                case "adduser":
                {
                    if (positional.Count < 2) throw new ArgumentException("adduser NAME ROLE [PASSWORD] [--windows DOMAIN\\user]");
                    using var ds = NpgsqlDataSource.Create(cs);
                    PgSchema.Migrate(ds);
                    var users = new UserStore(ds);
                    var password = positional.Count > 2 ? positional[2] : null;
                    var u = users.Create(positional[0], positional[1], password, positional[0], Flag("windows") ?? "");
                    Console.WriteLine($"User {u.UserName} created as {u.Role}{(password is null ? " (Windows sign-in only)" : "")}.");
                    return 0;
                }
                case "passwd":
                {
                    if (positional.Count < 2) throw new ArgumentException("passwd NAME PASSWORD");
                    using var ds = NpgsqlDataSource.Create(cs);
                    var users = new UserStore(ds);
                    var u = users.Find(positional[0]) ?? throw new ArgumentException($"No user {positional[0]}.");
                    users.Update(u.Id, new UserDto { Role = u.Role, DisplayName = u.DisplayName, WindowsAccount = u.WindowsAccount, Active = true, Password = positional[1] });
                    Console.WriteLine($"Password of {u.UserName} changed.");
                    return 0;
                }
                case "users":
                {
                    using var ds = NpgsqlDataSource.Create(cs);
                    foreach (var u in new UserStore(ds).All())
                        Console.WriteLine($"{u.UserName,-20} {u.Role,-9} {(u.Active ? "active" : "disabled"),-9} {u.WindowsAccount}");
                    return 0;
                }
            }
            return 1;
        }
    }
}
