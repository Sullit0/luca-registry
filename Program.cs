using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ──────────────────────────────────────────────────────────────
// Storage: SQLite embedded. Path configurable via LUCA_DB env var.
// Default: ./data/registry.db (Render creates working dir).
// ──────────────────────────────────────────────────────────────
var dbPath = Environment.GetEnvironmentVariable("LUCA_DB")
             ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "registry.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connString = $"Data Source={dbPath}";

// Initialize schema
using (var conn = new SqliteConnection(connString))
{
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS hosts (
            machine_id TEXT PRIMARY KEY,
            tunnel_url TEXT NOT NULL,
            local_ip TEXT,
            zerotier_ip TEXT,
            version TEXT,
            estudio_nombre TEXT,
            registered_at TEXT NOT NULL DEFAULT (datetime('now')),
            last_seen TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX IF NOT EXISTS idx_hosts_last_seen ON hosts(last_seen);
    """;
    cmd.ExecuteNonQuery();
}

// ──────────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────────
static string NormalizeMachineId(string id) => id.Trim().ToUpperInvariant();

static bool IsOnline(DateTime lastSeenUtc) =>
    DateTime.UtcNow - lastSeenUtc < TimeSpan.FromMinutes(15);

// ──────────────────────────────────────────────────────────────
// Endpoints
// ──────────────────────────────────────────────────────────────

app.MapGet("/", () => Results.Text(
    """
    Luca Registry — central registry for Luca on-premise instances.
    Endpoints:
      POST /registry/register
      POST /registry/heartbeat
      GET  /registry/locate/{machineId}
      GET  /registry/list
      GET  /health
    """, "text/plain"));

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// POST /registry/register — first-time registration
app.MapPost("/registry/register", async (RegisterRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId) || string.IsNullOrWhiteSpace(req.TunnelUrl))
        return Results.BadRequest(new { error = "machineId and tunnelUrl are required" });

    var id = NormalizeMachineId(req.MachineId);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO hosts (machine_id, tunnel_url, local_ip, zerotier_ip, version, estudio_nombre, registered_at, last_seen)
        VALUES ($id, $url, $local, $zt, $ver, $nom, datetime('now'), datetime('now'))
        ON CONFLICT(machine_id) DO UPDATE SET
            tunnel_url = excluded.tunnel_url,
            local_ip = excluded.local_ip,
            zerotier_ip = excluded.zerotier_ip,
            version = excluded.version,
            estudio_nombre = excluded.estudio_nombre,
            last_seen = datetime('now');
    """;
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$url", req.TunnelUrl.Trim());
    cmd.Parameters.AddWithValue("$local", (object?)req.LocalIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$zt", (object?)req.ZerotierIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$ver", (object?)req.Version ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$nom", (object?)req.EstudioNombre ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { machineId = id, registered = true });
});

// POST /registry/heartbeat — keep-alive + update location
app.MapPost("/registry/heartbeat", async (HeartbeatRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId) || string.IsNullOrWhiteSpace(req.TunnelUrl))
        return Results.BadRequest(new { error = "machineId and tunnelUrl are required" });

    var id = NormalizeMachineId(req.MachineId);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        UPDATE hosts SET
            tunnel_url = $url,
            local_ip = COALESCE($local, local_ip),
            zerotier_ip = COALESCE($zt, zerotier_ip),
            version = COALESCE($ver, version),
            estudio_nombre = COALESCE($nom, estudio_nombre),
            last_seen = datetime('now')
        WHERE machine_id = $id;
    """;
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$url", req.TunnelUrl.Trim());
    cmd.Parameters.AddWithValue("$local", (object?)req.LocalIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$zt", (object?)req.ZerotierIp ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$ver", (object?)req.Version ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$nom", (object?)req.EstudioNombre ?? DBNull.Value);
    var rows = await cmd.ExecuteNonQueryAsync();

    if (rows == 0)
        return Results.NotFound(new { error = "host not registered — call /registry/register first" });

    return Results.Ok(new { machineId = id, heartbeat = DateTime.UtcNow });
});

// GET /registry/locate/{machineId} — find a host
app.MapGet("/registry/locate/{machineId}", async (string machineId) =>
{
    var id = NormalizeMachineId(machineId);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT machine_id, tunnel_url, local_ip, zerotier_ip, version, estudio_nombre, registered_at, last_seen
        FROM hosts WHERE machine_id = $id;
    """;
    cmd.Parameters.AddWithValue("$id", id);
    using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Results.NotFound(new { error = "machine not found" });

    var lastSeen = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(7)), DateTimeKind.Utc);
    var registeredAt = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(6)), DateTimeKind.Utc);

    return Results.Ok(new LocateResponse(
        MachineId: reader.GetString(0),
        TunnelUrl: reader.GetString(1),
        LocalIp: reader.IsDBNull(2) ? null : reader.GetString(2),
        ZerotierIp: reader.IsDBNull(3) ? null : reader.GetString(3),
        Version: reader.IsDBNull(4) ? null : reader.GetString(4),
        EstudioNombre: reader.IsDBNull(5) ? null : reader.GetString(5),
        RegisteredAt: registeredAt,
        LastSeen: lastSeen,
        Status: IsOnline(lastSeen) ? "online" : "offline"));
});

// GET /registry/list — admin/debug view (returns all hosts)
app.MapGet("/registry/list", async () =>
{
    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT machine_id, tunnel_url, version, estudio_nombre, registered_at, last_seen
        FROM hosts ORDER BY last_seen DESC;
    """;
    using var reader = await cmd.ExecuteReaderAsync();

    var hosts = new List<object>();
    while (await reader.ReadAsync())
    {
        var lastSeen = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(5)), DateTimeKind.Utc);
        hosts.Add(new
        {
            machineId = reader.GetString(0),
            tunnelUrl = reader.GetString(1),
            version = reader.IsDBNull(2) ? null : reader.GetString(2),
            estudioNombre = reader.IsDBNull(3) ? null : reader.GetString(3),
            registeredAt = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(4)), DateTimeKind.Utc),
            lastSeen,
            status = IsOnline(lastSeen) ? "online" : "offline"
        });
    }
    return Results.Ok(hosts);
});

// ──────────────────────────────────────────────────────────────
// Run — Render provides PORT env var
// ──────────────────────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");


// ──────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────
public record RegisterRequest(
    string MachineId,
    string TunnelUrl,
    string? LocalIp = null,
    string? ZerotierIp = null,
    string? Version = null,
    string? EstudioNombre = null);

public record HeartbeatRequest(
    string MachineId,
    string TunnelUrl,
    string? LocalIp = null,
    string? ZerotierIp = null,
    string? Version = null,
    string? EstudioNombre = null);

public record LocateResponse(
    string MachineId,
    string TunnelUrl,
    string? LocalIp,
    string? ZerotierIp,
    string? Version,
    string? EstudioNombre,
    DateTime RegisteredAt,
    DateTime LastSeen,
    string Status);
